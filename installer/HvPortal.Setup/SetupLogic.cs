// Logic of the graphical setup (HvPortal.Gateway.Setup.exe / HvPortal.Agent.Setup.exe), without UI: validation of the
// values typed in the wizard and the parameters handed to the installer scripts. Compiled into the setup (net472) and,
// as a linked file, into the gateway test project.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HvPortal.Setup
{
    /// <summary>Format rules of the wizard fields. Everything that reaches a script parameter passes through here.</summary>
    public static class SetupRules
    {
        private static readonly Regex Dns = new Regex(
            @"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant);
        private static readonly Regex Ip = new Regex(
            @"^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$", RegexOptions.CultureInvariant);
        private static readonly Regex Hex = new Regex("^[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Account = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$", RegexOptions.CultureInvariant);
        private static readonly Regex TokenChars = new Regex(@"^[A-Za-z0-9_\-.~+/=]{16,512}$", RegexOptions.CultureInvariant);
        private static readonly string[] FirewallKeywords = { "Any", "LocalSubnet", "DNS", "DHCP", "WINS", "DefaultGateway", "Internet", "Intranet" };

        public static bool IsIpv4(string? s) => s != null && Ip.IsMatch(s);

        public static bool IsHostName(string? s) => s != null && (Ip.IsMatch(s) || (Dns.IsMatch(s) && !s.All(c => char.IsDigit(c) || c == '.')));

        public static bool IsPort(int port) => port >= 1 && port <= 65535;

        /// <summary>Certificate thumbprint (SHA-1, 40 hex digits; spaces as copied from the certificate dialog are removed first).</summary>
        public static string? NormalizeThumbprint(string? s)
        {
            var t = new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '‎').ToArray());
            return t.Length == 40 && Hex.IsMatch(t) ? t.ToUpperInvariant() : null;
        }

        /// <summary>SHA-256 fingerprint of the gateway CA (64 hex digits; colons, dashes and spaces are removed first).</summary>
        public static string? NormalizeFingerprint(string? s)
        {
            var t = new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
            return t.Length == 64 && Hex.IsMatch(t) ? t.ToUpperInvariant() : null;
        }

        public static bool IsToken(string? s) => s != null && TokenChars.IsMatch(s);

        public static bool IsServiceAccount(string? s) => s != null && Account.IsMatch(s);

        /// <summary>Remote address of a firewall rule: keyword, IPv4 address, subnet (a.b.c.d/nn) or range (a.b.c.d-e.f.g.h).</summary>
        public static bool IsFirewallAddress(string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            if (FirewallKeywords.Contains(s, StringComparer.OrdinalIgnoreCase) || Ip.IsMatch(s))
            {
                return true;
            }
            var slash = s!.Split('/');
            if (slash.Length == 2)
            {
                return Ip.IsMatch(slash[0]) && int.TryParse(slash[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits >= 0 && bits <= 32;
            }
            var range = s.Split('-');
            return range.Length == 2 && Ip.IsMatch(range[0]) && Ip.IsMatch(range[1]);
        }

        /// <summary>https://name:port without path, query or credentials. Returns the canonical form or null.</summary>
        public static string? NormalizeGatewayUrl(string? s)
        {
            var text = (s ?? "").Trim().TrimEnd('/');
            const string scheme = "https://";
            if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var rest = text.Substring(scheme.Length);
            var colon = rest.LastIndexOf(':');
            var host = colon < 0 ? rest : rest.Substring(0, colon);
            var port = 8444;
            if (colon >= 0 && !(int.TryParse(rest.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) && IsPort(port)))
            {
                return null;
            }
            return IsHostName(host) ? scheme + host.ToLowerInvariant() + ":" + port.ToString(CultureInfo.InvariantCulture) : null;
        }

        private static readonly string[] BlockedRootFolders =
        {
            "Windows", "Users", "User", "inetpub", "VBRCatalog", "ovhupd", "Packages", "PerfLogs", "Program Files", "Program Files (x86)", "ProgramData",
            "Recovery", "Boot", "System Volume Information", "Documents and Settings", "Config.Msi", "MSOCache", "OneDriveTemp", "Intel", "AMD", "NVIDIA",
        };

        private static readonly Regex LocalFolder = new Regex(@"^[A-Za-z]:\\[^\x00-\x1f""*?<>|:/]{0,197}$", RegexOptions.CultureInvariant);

        /// <summary>A storage folder for Install-Agent -StorageFolders: local path, not a system folder, no traversal.</summary>
        public static bool IsStorageFolder(string? s)
        {
            if (s == null || !LocalFolder.IsMatch(s) || s.Contains("..") || s.Contains("\\\\"))
            {
                return false;
            }
            var segments = s.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length < 2 || !(segments[1].StartsWith("$", StringComparison.Ordinal)
                                            || BlockedRootFolders.Contains(segments[1], StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Folders typed one per line (or separated by ';'): spaces are part of folder names.</summary>
        public static List<string> SplitFolders(string? text) =>
            (text ?? "").Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)
                .Select(x => x.Length > 3 ? x.TrimEnd('\\') : x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Splits a list typed with commas, semicolons, spaces or new lines.</summary>
        public static List<string> SplitList(string? text) =>
            (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Choices of the gateway wizard = parameters of Install-Gateway.ps1.</summary>
    public sealed class GatewaySetupOptions
    {
        public List<string> PublicNames { get; set; } = new List<string>();
        public int UiPort { get; set; } = 8443;
        public int AgentPort { get; set; } = 8444;
        public string BindAddress { get; set; } = "0.0.0.0";
        /// <summary>Null = internal certificate.</summary>
        public string? UiCertificateThumbprint { get; set; }
        public bool OpenFirewall { get; set; }
        public List<string> UiRemoteAddress { get; set; } = new List<string> { "LocalSubnet" };
        public List<string> AgentRemoteAddress { get; set; } = new List<string> { "LocalSubnet" };

        /// <summary>Error codes (keys of the setup texts); empty when everything is valid.</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (PublicNames.Count == 0 || PublicNames.Count > 16 || PublicNames.Any(n => !SetupRules.IsHostName(n) || n.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("err.names");
            }
            if (!SetupRules.IsPort(UiPort) || !SetupRules.IsPort(AgentPort) || UiPort == AgentPort)
            {
                errors.Add("err.ports");
            }
            if (!SetupRules.IsIpv4(BindAddress))
            {
                errors.Add("err.bind");
            }
            if (UiCertificateThumbprint != null && SetupRules.NormalizeThumbprint(UiCertificateThumbprint) == null)
            {
                errors.Add("err.thumbprint");
            }
            if (OpenFirewall && (UiRemoteAddress.Count == 0 || AgentRemoteAddress.Count == 0
                                 || UiRemoteAddress.Concat(AgentRemoteAddress).Any(a => !SetupRules.IsFirewallAddress(a))))
            {
                errors.Add("err.firewall");
            }
            return errors;
        }

        public Dictionary<string, object> ToParameters(string source)
        {
            var p = new Dictionary<string, object>
            {
                ["Source"] = source,
                ["PublicNames"] = PublicNames.ToArray(),
                ["UiPort"] = UiPort,
                ["AgentPort"] = AgentPort,
                ["BindAddress"] = BindAddress,
            };
            if (SetupRules.NormalizeThumbprint(UiCertificateThumbprint) is string thumbprint)
            {
                p["UiCertificateThumbprint"] = thumbprint;
            }
            if (OpenFirewall)
            {
                p["OpenFirewall"] = true;
                p["UiRemoteAddress"] = UiRemoteAddress.ToArray();
                p["AgentRemoteAddress"] = AgentRemoteAddress.ToArray();
            }
            return p;
        }

        /// <summary>
        /// Settings of an installed gateway, from the service environment (registry value "Environment" of the service:
        /// lines NAME=value written by Install-Gateway.ps1). Used to pre-fill the wizard on upgrade.
        /// </summary>
        public static GatewaySetupOptions FromServiceEnvironment(IEnumerable<string>? environment)
        {
            var o = new GatewaySetupOptions();
            var names = new SortedDictionary<int, string>();
            foreach (var line in environment ?? Enumerable.Empty<string>())
            {
                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var key = line.Substring(0, eq);
                var value = line.Substring(eq + 1).Trim();
                const string namePrefix = "Gateway__PublicNames__";
                if (key.Equals("Gateway__UiPort", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var ui) && SetupRules.IsPort(ui))
                {
                    o.UiPort = ui;
                }
                else if (key.Equals("Gateway__AgentPort", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var agent) && SetupRules.IsPort(agent))
                {
                    o.AgentPort = agent;
                }
                else if (key.Equals("Gateway__BindAddress", StringComparison.OrdinalIgnoreCase) && SetupRules.IsIpv4(value))
                {
                    o.BindAddress = value;
                }
                else if (key.Equals("Gateway__UiCertificateThumbprint", StringComparison.OrdinalIgnoreCase))
                {
                    o.UiCertificateThumbprint = SetupRules.NormalizeThumbprint(value);
                }
                else if (key.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(key.Substring(namePrefix.Length), out var index)
                         && SetupRules.IsHostName(value))
                {
                    names[index] = value;
                }
            }
            o.PublicNames = names.Values.ToList();
            return o;
        }
    }

    /// <summary>Choices of the agent wizard = parameters of Install-Agent.ps1 (the token travels in an environment variable).</summary>
    public sealed class AgentSetupOptions
    {
        /// <summary>Management server (no Hyper-V): the agent reaches remote hosts with the credentials stored in the portal.</summary>
        public bool Management { get; set; }
        /// <summary>False on an upgrade that keeps the current registration.</summary>
        public bool Enroll { get; set; } = true;
        public string Gateway { get; set; } = "";
        public string CaSha256 { get; set; } = "";
        public string Token { get; set; } = "";
        public bool ForceReenroll { get; set; }
        /// <summary>Null = virtual service account (or the account already in use on upgrade).</summary>
        public string? ServiceAccount { get; set; }
        /// <summary>
        /// Folders the portal may use on this host (Install-Agent -StorageFolders). Null = not passed (new install: no
        /// restriction; upgrade: keep the current list); empty = remove the restriction.
        /// </summary>
        public List<string>? StorageFolders { get; set; }
        /// <summary>No updater task: the agent can only be updated by hand (Install-Agent -DisableRemoteUpdate).</summary>
        public bool DisableRemoteUpdate { get; set; }
        /// <summary>
        /// Node of a failover cluster (Install-Agent -ClusterAccess): "Full", "ReadOnly" or "None" (removes the access).
        /// Null = not passed (nothing changes).
        /// </summary>
        public string? ClusterAccess { get; set; }

        public static readonly string[] ClusterAccessValues = { "Full", "ReadOnly", "None" };

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (ClusterAccess != null && (Management || Array.IndexOf(ClusterAccessValues, ClusterAccess) < 0))
            {
                errors.Add("err.cluster");
            }
            if (StorageFolders != null && (StorageFolders.Count > 16 || StorageFolders.Any(f => !SetupRules.IsStorageFolder(f))))
            {
                errors.Add("err.storage");
            }
            if (Enroll)
            {
                if (SetupRules.NormalizeGatewayUrl(Gateway) == null)
                {
                    errors.Add("err.gateway");
                }
                if (SetupRules.NormalizeFingerprint(CaSha256) == null)
                {
                    errors.Add("err.fingerprint");
                }
                if (!SetupRules.IsToken(Token.Trim()))
                {
                    errors.Add("err.token");
                }
            }
            if (ServiceAccount != null && !SetupRules.IsServiceAccount(ServiceAccount))
            {
                errors.Add("err.account");
            }
            return errors;
        }

        public Dictionary<string, object> ToParameters(string source)
        {
            var p = new Dictionary<string, object> { ["Source"] = source };
            if (Enroll)
            {
                p["Gateway"] = SetupRules.NormalizeGatewayUrl(Gateway) ?? "";
                p["CaSha256"] = SetupRules.NormalizeFingerprint(CaSha256) ?? "";
                if (ForceReenroll)
                {
                    p["ForceReenroll"] = true;
                }
            }
            if (Management)
            {
                p["Management"] = true;
            }
            if (ServiceAccount != null)
            {
                p["ServiceAccount"] = ServiceAccount;
            }
            if (StorageFolders != null)
            {
                // An empty value removes the restriction (the script ignores empty entries).
                p["StorageFolders"] = StorageFolders.Count == 0 ? new[] { "" } : StorageFolders.ToArray();
            }
            if (DisableRemoteUpdate)
            {
                p["DisableRemoteUpdate"] = true;
            }
            if (ClusterAccess != null && !Management)
            {
                p["ClusterAccess"] = ClusterAccess;
            }
            return p;
        }
    }

    /// <summary>
    /// Connection data copied from the portal (Add host / New management agent): either the one-line setup string
    /// "HVPORTAL-SETUP gateway=... ca=... token=... role=host|management [cluster=full]" or the PowerShell command of the guide.
    /// </summary>
    public static class PortalData
    {
        public const string Prefix = "HVPORTAL-SETUP";

        public static string Format(string gateway, string caSha256, string token, bool management, bool clusterNode = false) =>
            Prefix + " gateway=" + gateway + " ca=" + caSha256 + " token=" + token + " role=" + (management ? "management" : "host")
            + (clusterNode && !management ? " cluster=full" : "");

        public static bool TryParse(string? text, out AgentSetupOptions options)
        {
            options = new AgentSetupOptions();
            if (string.IsNullOrWhiteSpace(text) || text!.Length > 8192)
            {
                return false;
            }
            string? gateway = null, ca = null, token = null;
            var management = false;
            string? cluster = null;
            var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                foreach (var part in text.Substring(start + Prefix.Length).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    var key = part.Substring(0, eq).ToLowerInvariant();
                    var value = part.Substring(eq + 1);
                    if (key == "gateway") gateway = value;
                    else if (key == "ca") ca = value;
                    else if (key == "token") token = value;
                    else if (key == "role") management = value.Equals("management", StringComparison.OrdinalIgnoreCase);
                    else if (key == "cluster") cluster = value.Equals("full", StringComparison.OrdinalIgnoreCase) ? "Full"
                        : value.Equals("readonly", StringComparison.OrdinalIgnoreCase) ? "ReadOnly" : null;
                }
            }
            else
            {
                // The PowerShell command of the guide: $env:HVP_ENROLLMENT_TOKEN = '<token>' ... -Gateway <url> -CaSha256 <hex> [-Management]
                gateway = Regex.Match(text, @"-Gateway\s+(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
                ca = Regex.Match(text, @"-CaSha256\s+(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
                token = Regex.Match(text, @"HVP_ENROLLMENT_TOKEN\s*=\s*'([^']+)'", RegexOptions.IgnoreCase).Groups[1].Value;
                management = Regex.IsMatch(text, @"-Management\b", RegexOptions.IgnoreCase);
                var access = Regex.Match(text, @"-ClusterAccess\s+(Full|ReadOnly)\b", RegexOptions.IgnoreCase);
                cluster = !access.Success ? null : access.Groups[1].Value.Equals("full", StringComparison.OrdinalIgnoreCase) ? "Full" : "ReadOnly";
            }
            var url = SetupRules.NormalizeGatewayUrl(gateway);
            var fingerprint = SetupRules.NormalizeFingerprint(ca);
            if (url == null || fingerprint == null || !SetupRules.IsToken(token))
            {
                return false;
            }
            options = new AgentSetupOptions { Gateway = url, CaSha256 = fingerprint, Token = token!, Management = management, ClusterAccess = management ? null : cluster };
            return true;
        }
    }

    /// <summary>
    /// Minimal JSON writer for the parameters file read by installer\Invoke-Setup.ps1 (strings, integers, booleans and
    /// string arrays). The values never touch a command line, so no shell quoting is involved.
    /// </summary>
    public static class SetupJson
    {
        public static string Serialize(IDictionary<string, object> values)
        {
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var pair in values)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                Quote(sb, pair.Key);
                sb.Append(':');
                switch (pair.Value)
                {
                    case bool b:
                        sb.Append(b ? "true" : "false");
                        break;
                    case int i:
                        sb.Append(i.ToString(CultureInfo.InvariantCulture));
                        break;
                    case string s:
                        Quote(sb, s);
                        break;
                    case IEnumerable<string> list:
                        sb.Append('[');
                        var firstItem = true;
                        foreach (var item in list)
                        {
                            if (!firstItem)
                            {
                                sb.Append(',');
                            }
                            firstItem = false;
                            Quote(sb, item);
                        }
                        sb.Append(']');
                        break;
                    default:
                        throw new ArgumentException("Unsupported parameter type: " + pair.Key, nameof(values));
                }
            }
            return sb.Append('}').ToString();
        }

        private static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c > '~')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
