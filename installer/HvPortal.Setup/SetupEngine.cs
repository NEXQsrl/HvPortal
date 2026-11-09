#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HvPortal.Setup
{
    /// <summary>What the setup finds on this server and the work it does: unpack the payload and run the installer script.</summary>
    internal static class SetupEngine
    {
#if SETUP_GATEWAY
        public static readonly bool IsGateway = true;
        public const string Component = "Gateway";
#else
        public static readonly bool IsGateway = false;
        public const string Component = "Agent";
#endif
        public static string ServiceName => "HvPortal" + Component;
        public static string InstallPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HvPortal", Component);
        public static string DataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvPortal", Component);

        public static string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.zip");

        /// <summary>The Cluster service runs: this machine is a node of a failover cluster (the agent may need access to it).</summary>
        public static bool ClusterServiceRunning()
        {
            try
            {
                using (var service = new System.ServiceProcess.ServiceController("ClusSvc"))
                {
                    return service.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                }
            }
            catch (InvalidOperationException)
            {
                return false; // not installed
            }
        }

        // ------------------------------------------------------------------ existing installation

        public static bool ServiceExists(string name)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                return key != null;
            }
        }

        /// <summary>Lines NAME=value of the service environment written by the installer scripts.</summary>
        public static string[] ServiceEnvironment(string name)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                return key?.GetValue("Environment") as string[] ?? new string[0];
            }
        }

        /// <summary>Account of the service ("NT SERVICE\HvPortalAgent", ".\svc-hvportal", "LocalSystem"...), or null.</summary>
        public static string? ServiceAccount(string name)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                return key?.GetValue("ObjectName") as string;
            }
        }

        /// <summary>The installed agent is enrolled (agent.json present) and, when readable, whether as management agent.</summary>
        public static (bool Enrolled, bool Management) AgentEnrollment()
        {
            var dir = ServiceEnvironment("HvPortalAgent").Select(l => l.Split(new[] { '=' }, 2)).Where(p => p.Length == 2 && p[0] == "HVP_AGENT_DATA_DIR")
                .Select(p => p[1]).FirstOrDefault() ?? DataPath;
            var state = Path.Combine(dir, "agent.json");
            try
            {
                if (!File.Exists(state))
                {
                    return (false, false);
                }
                var json = File.ReadAllText(state);
                return (true, Regex.IsMatch(json, "\"Role\"\\s*:\\s*(1|\"Management\")", RegexOptions.IgnoreCase));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return (false, false);
            }
        }

        /// <summary>Default names for a new gateway: computer name, DNS name and IPv4 addresses of this server.</summary>
        public static List<string> DefaultPublicNames()
        {
            var names = new List<string>();
            void Add(string? n)
            {
                if (!string.IsNullOrWhiteSpace(n) && SetupRules.IsHostName(n) && !names.Contains(n!, StringComparer.OrdinalIgnoreCase)) names.Add(n!.ToLowerInvariant());
            }
            try
            {
                Add(Dns.GetHostEntry(Dns.GetHostName()).HostName);
            }
            catch (SocketException)
            {
                // No DNS name: the computer name and the addresses are enough.
            }
            Add(Environment.MachineName);
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    foreach (var a in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                    {
                        var ip = a.Address.ToString();
                        if (!ip.StartsWith("169.254.", StringComparison.Ordinal)) Add(ip);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Addresses can be typed by hand.
            }
            return names;
        }

        /// <summary>
        /// True when something listens on the TCP port. The caller excludes the ports the running gateway already
        /// holds; the installer script repeats the check per owning process before it stops the service.
        /// </summary>
        public static bool PortBusy(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
            }
            catch (NetworkInformationException)
            {
                return false;
            }
        }

        public static bool ServiceRunning(string name)
        {
            try
            {
                using (var service = new System.ServiceProcess.ServiceController(name))
                {
                    return service.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static bool HyperVPresent() => ServiceExists("vmms");

        // ------------------------------------------------------------------ certificates

        public sealed class CertificateChoice
        {
            public string Thumbprint = "";
            public string Display = "";
            public override string ToString() => Display;
        }

        /// <summary>Certificates of LocalMachine\My usable for the portal: private key, not expired, server authentication (or no EKU).</summary>
        public static List<CertificateChoice> ServerCertificates()
        {
            var list = new List<CertificateChoice>();
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                foreach (var c in store.Certificates)
                {
                    var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                    var server = eku == null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
                    if (c.HasPrivateKey && c.NotAfter > DateTime.Now && server)
                    {
                        var name = c.GetNameInfo(X509NameType.DnsName, false);
                        if (string.IsNullOrEmpty(name)) name = c.GetNameInfo(X509NameType.SimpleName, false);
                        list.Add(new CertificateChoice
                        {
                            Thumbprint = c.Thumbprint ?? "",
                            Display = name + "  —  " + Strings.T("cert.expires", c.NotAfter.ToString("d", CultureInfo.CurrentCulture)) + "  —  " + (c.Thumbprint ?? "").Substring(0, 8) + "…",
                        });
                    }
                }
            }
            return list.OrderBy(c => c.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Imports a PFX into LocalMachine\My (machine key, not exportable) and returns its thumbprint.</summary>
        public static string ImportPfx(string file, string password)
        {
            var cert = new X509Certificate2(file, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            if (!cert.HasPrivateKey)
            {
                throw new InvalidOperationException(Strings.T("cert.noKey"));
            }
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
            }
            return cert.Thumbprint;
        }

        // ------------------------------------------------------------------ payload and installation

        /// <summary>
        /// Unpacks the payload into a new folder under Program Files\HvPortal\Setup that only administrators and SYSTEM
        /// can touch (the scripts run elevated from there: nobody else may change them in between).
        /// </summary>
        public static string ExtractPayload(string? destination = null)
        {
            string target;
            if (destination != null)
            {
                target = Path.GetFullPath(destination);
                Directory.CreateDirectory(target);
            }
            else
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HvPortal", "Setup");
                target = Path.Combine(root, Component + "-" + Guid.NewGuid().ToString("N"));
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                {
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                }
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(target, security);
            }
            var prefix = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException(Strings.T("noPayload")))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Invalid entry in the setup payload: " + entry.FullName);
                    }
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, true);
                }
            }
            return target;
        }

        public static void DeleteFolder(string? folder)
        {
            try
            {
                if (folder != null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Left behind (only administrators can read it); removed by the next setup run or by hand.
            }
        }

        /// <summary>
        /// Runs installer\Invoke-Setup.ps1 from the unpacked payload with the chosen parameters (JSON file) and reports
        /// every output line. Returns the exit code of PowerShell (0 = installed).
        /// </summary>
        public static int RunInstaller(string staging, IDictionary<string, object> parameters, string? enrollmentToken, Action<string> output)
        {
            var wrapper = Path.Combine(staging, "installer", "Invoke-Setup.ps1");
            if (!File.Exists(wrapper))
            {
                throw new FileNotFoundException("installer\\Invoke-Setup.ps1 is missing from the setup payload.");
            }
            var parametersFile = Path.Combine(staging, "setup-parameters.json");
            File.WriteAllText(parametersFile, SetupJson.Serialize(parameters), new UTF8Encoding(false));
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(powershell)
            {
                // Paths are ours (Program Files\HvPortal\Setup\<component>-<guid>): no user text reaches the command line.
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + wrapper + "\" -Component " + Component
                            + " -ParametersFile \"" + parametersFile + "\"",
                WorkingDirectory = Path.Combine(staging, "installer"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            if (enrollmentToken != null)
            {
                psi.EnvironmentVariables["HVP_ENROLLMENT_TOKEN"] = enrollmentToken;
            }
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) output(e.Data); };
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        /// <summary>The one-time setup token of a new gateway (second line of bootstrap\setup-token.txt), or null.</summary>
        public static (string? Token, string File) GatewaySetupToken()
        {
            var file = Path.Combine(DataPath, "bootstrap", "setup-token.txt");
            try
            {
                if (File.Exists(file))
                {
                    var token = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1).FirstOrDefault();
                    return (token, file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Shown as "open the file" instead.
            }
            return (null, file);
        }

        /// <summary>Writes the installation log next to the data of the product (best effort) and returns its path.</summary>
        public static string? SaveLog(IEnumerable<string> lines)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvPortal", "SetupLogs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, Component + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
