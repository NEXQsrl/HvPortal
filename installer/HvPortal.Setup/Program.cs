#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace HvPortal.Setup
{
    internal static class Program
    {
        /// <summary>
        /// (no arguments)        graphical setup; asks for elevation (UAC) first.
        /// --extract &lt;folder&gt;    unpacks the payload (binaries + installer scripts) for scripted installations.
        /// --render &lt;folder&gt;     saves a picture of every wizard page (no installation, no elevation).
        /// --lang it|en          language of the wizard (default: Windows display language).
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var lang = Value(args, "--lang");
            if (lang != null)
            {
                Strings.Italian = lang.Equals("it", StringComparison.OrdinalIgnoreCase);
            }
            var title = Strings.T(SetupEngine.IsGateway ? "title.gateway" : "title.agent");
            try
            {
                if (Value(args, "--render") is string renderFolder)
                {
                    Wizard.RenderAll(renderFolder);
                    return 0;
                }
                if (!SetupEngine.HasPayload)
                {
                    MessageBox.Show(Strings.T("noPayload"), title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                if (Value(args, "--extract") is string extractFolder)
                {
                    SetupEngine.ExtractPayload(extractFolder);
                    return 0;
                }
                if (!SetupEngine.IsAdministrator())
                {
                    return Elevate(args, title);
                }
                using (var wizard = new Wizard())
                {
                    Application.Run(wizard);
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.GetType().Name + ": " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string? Value(string[] args, string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Starts this same file again as administrator (the Windows consent prompt appears).</summary>
        private static int Elevate(string[] args, string title)
        {
            try
            {
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "") + "\"")),
                };
                using (Process.Start(psi))
                {
                    return 0;
                }
            }
            catch (Win32Exception)
            {
                // The consent prompt was refused.
                MessageBox.Show(Strings.T("needAdmin"), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
        }
    }
}
