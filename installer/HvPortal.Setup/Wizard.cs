#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HvPortal.Setup
{
    /// <summary>Lays controls out top to bottom inside a page (fixed width, measured heights).</summary>
    internal sealed class Column
    {
        public readonly Panel Panel;
        public int Y;
        private readonly int _width;
        // Explicit fonts: heights are measured before the controls join the form (and inherit its font).
        private static readonly Font Base = new Font("Segoe UI", 9f);
        private static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Color Muted = Color.FromArgb(95, 99, 104);

        public Column(int width, int height)
        {
            _width = width;
            Panel = new Panel { Size = new Size(width, height), Visible = false };
        }

        public T Add<T>(T control, int height, int indent = 0, int width = -1) where T : Control
        {
            control.Location = new Point(indent, Y);
            control.Size = new Size(width < 0 ? _width - indent : width, height);
            Panel.Controls.Add(control);
            Y += height;
            return control;
        }

        public Label Text(string text, bool muted = false, bool bold = false, int indent = 0)
        {
            var label = new Label { Text = text, AutoSize = false, ForeColor = muted ? Muted : SystemColors.ControlText, UseMnemonic = false, Font = bold ? Bold : Base };
            var size = TextRenderer.MeasureText(text, label.Font, new Size(_width - indent, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Add(label, size.Height + 2, indent);
            return label;
        }

        public TextBox Input(string value, int width = -1, bool password = false, int indent = 0)
        {
            var box = new TextBox { Text = value, UseSystemPasswordChar = password };
            Add(box, 23, indent, width);
            Y += 2;
            return box;
        }

        public RadioButton Radio(string text, bool check, int indent = 0)
        {
            var size = TextRenderer.MeasureText(text, Base, new Size(_width - indent - 22, int.MaxValue), TextFormatFlags.WordBreak);
            return Add(new RadioButton { Font = Base, Text = text, Checked = check, UseMnemonic = false, CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft }, Math.Max(20, size.Height + 4), indent);
        }

        public CheckBox Check(string text, bool check, int indent = 0)
        {
            var size = TextRenderer.MeasureText(text, Base, new Size(_width - indent - 22, int.MaxValue), TextFormatFlags.WordBreak);
            return Add(new CheckBox { Font = Base, Text = text, Checked = check, UseMnemonic = false, CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft }, Math.Max(20, size.Height + 4), indent);
        }

        public void Gap(int pixels = 10) => Y += pixels;
    }

    internal sealed class WizardPage
    {
        public string Title = "";
        public string Subtitle = "";
        public Column Column = null!;
        /// <summary>Reads the controls into the options; returns an error text or null.</summary>
        public Func<string?>? Leave;
        public Action? Enter;
    }

    /// <summary>
    /// The setup wizard. Gateway: addresses and ports, certificate (internal / store / PFX import), firewall. Agent:
    /// host or management server, connection data pasted from the portal, service account. The last page runs the
    /// installer script and shows its output.
    /// </summary>
    internal sealed class Wizard : Form
    {
        private const int ContentWidth = 612;
        private const int ContentHeight = 356;

        private readonly List<WizardPage> _pages = new List<WizardPage>();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Panel _content = new Panel();
        private readonly Button _back = new Button();
        private readonly Button _next = new Button();
        private readonly Button _cancel = new Button();
        private int _index;
        private bool _running;
        private bool _finished;
        private string? _staging;

        private readonly bool _upgrade = SetupEngine.ServiceExists(SetupEngine.ServiceName);
        private readonly GatewaySetupOptions _gateway;
        private readonly AgentSetupOptions _agent = new AgentSetupOptions();
        private readonly bool _agentEnrolled;
        private bool _keepAccount;
        private readonly int[] _ownPorts = Array.Empty<int>();

        // Run page
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly Panel _result = new Panel();
        private readonly List<string> _logLines = new List<string>();

        public Wizard()
        {
            Text = Strings.T(SetupEngine.IsGateway ? "title.gateway" : "title.agent");
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 500);
            BackColor = SystemColors.Control;

            _gateway = _upgrade && SetupEngine.IsGateway
                ? GatewaySetupOptions.FromServiceEnvironment(SetupEngine.ServiceEnvironment(SetupEngine.ServiceName))
                : new GatewaySetupOptions { OpenFirewall = true };
            if (_upgrade && SetupEngine.IsGateway && SetupEngine.ServiceRunning(SetupEngine.ServiceName))
            {
                // Ports the gateway being updated listens on: in use, but by the gateway itself.
                _ownPorts = new[] { _gateway.UiPort, _gateway.AgentPort };
            }
            if (_gateway.PublicNames.Count == 0)
            {
                _gateway.PublicNames = SetupEngine.DefaultPublicNames();
            }
            if (!SetupEngine.IsGateway && _upgrade)
            {
                var (enrolled, management) = SetupEngine.AgentEnrollment();
                _agentEnrolled = enrolled;
                _agent.Management = management;
                _agent.Enroll = !enrolled;
                var account = SetupEngine.ServiceAccount(SetupEngine.ServiceName) ?? "";
                _keepAccount = !account.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase) && !account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) && account.Length > 0;
            }
            else if (!SetupEngine.IsGateway)
            {
                _agent.Management = !SetupEngine.HyperVPresent();
            }

            BuildFrame();
            _pages.Add(WelcomePage());
            if (SetupEngine.IsGateway)
            {
                _pages.Add(NetworkPage());
                _pages.Add(CertificatePage());
                _pages.Add(FirewallPage());
            }
            else
            {
                _pages.Add(ModePage());
                _pages.Add(EnrollPage());
                _pages.Add(AccountPage());
                _pages.Add(StoragePage());
            }
            _pages.Add(SummaryPage());
            _pages.Add(RunPage());
            foreach (var p in _pages)
            {
                _content.Controls.Add(p.Column.Panel);
            }
            Show(0);
        }

        // ------------------------------------------------------------------ frame

        private void BuildFrame()
        {
            var header = new Panel { BackColor = Color.White, Location = new Point(0, 0), Size = new Size(660, 66) };
            _title.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _title.Location = new Point(24, 12);
            _title.Size = new Size(612, 24);
            _subtitle.ForeColor = Color.FromArgb(95, 99, 104);
            _subtitle.Location = new Point(24, 38);
            _subtitle.Size = new Size(612, 18);
            header.Controls.Add(_title);
            header.Controls.Add(_subtitle);
            Controls.Add(header);
            Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(0, 66), Size = new Size(660, 2) });
            _content.Location = new Point(24, 82);
            _content.Size = new Size(ContentWidth, ContentHeight);
            Controls.Add(_content);
            Controls.Add(new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(0, 450), Size = new Size(660, 2) });
            var version = new Label { Text = "HvPortal " + SetupEngine.Version + "  ·  NEXQ S.r.l.", ForeColor = Color.FromArgb(120, 124, 128), Location = new Point(24, 468), Size = new Size(260, 18) };
            Controls.Add(version);
            void Place(Button b, int x, string text)
            {
                b.Text = text;
                b.Location = new Point(x, 462);
                b.Size = new Size(96, 27);
                Controls.Add(b);
            }
            Place(_back, 340, Strings.T("back"));
            Place(_next, 440, Strings.T("next"));
            Place(_cancel, 546, Strings.T("cancel"));
            _back.Click += (_, __) => Show(_index - 1);
            _next.Click += (_, __) => Next();
            _cancel.Click += (_, __) => Close();
            AcceptButton = _next;
        }

        private void Show(int index)
        {
            _index = index;
            for (var i = 0; i < _pages.Count; i++)
            {
                _pages[i].Column.Panel.Visible = i == index;
            }
            var page = _pages[index];
            _title.Text = page.Title;
            _subtitle.Text = page.Subtitle;
            page.Enter?.Invoke();
            var last = index == _pages.Count - 1;
            var summary = index == _pages.Count - 2;
            _back.Visible = !last;
            _back.Enabled = index > 0;
            _next.Visible = !last;
            _next.Text = summary ? Strings.T(_upgrade ? "upgrade" : "install") : Strings.T("next");
            _cancel.Text = Strings.T(last ? "close" : "cancel");
            _cancel.Enabled = !_running;
        }

        private void Next()
        {
            var error = _pages[_index].Leave?.Invoke();
            if (error != null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Show(_index + 1);
            if (_index == _pages.Count - 1)
            {
                StartInstallation();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                e.Cancel = true;
                return;
            }
            if (!_finished && _index > 0 && MessageBox.Show(this, Strings.T("cancelConfirm"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SetupEngine.DeleteFolder(_staging);
            base.OnFormClosing(e);
        }

        private WizardPage Page(string titleKey, string? subtitleKey, Action<Column> build)
        {
            var column = new Column(ContentWidth, ContentHeight);
            build(column);
            return new WizardPage { Title = Strings.T(titleKey), Subtitle = subtitleKey == null ? "" : Strings.T(subtitleKey), Column = column };
        }

        // ------------------------------------------------------------------ pages: common

        private WizardPage WelcomePage() => Page("welcome.title", null, c =>
        {
            c.Text(Strings.T(SetupEngine.IsGateway ? "welcome.gateway" : "welcome.agent"));
            c.Gap(12);
            c.Text(_upgrade
                ? Strings.T(SetupEngine.IsGateway ? "welcome.upgradeGateway" : "welcome.upgradeAgent")
                : Strings.T("welcome.new", SetupEngine.InstallPath, SetupEngine.ServiceName));
            c.Gap(12);
            c.Text(Strings.T("version", SetupEngine.Version), muted: true);
            c.Gap(18);
            c.Text(Strings.T("welcome.scripts"), muted: true);
        });

        // ------------------------------------------------------------------ pages: gateway

        private WizardPage NetworkPage()
        {
            TextBox names = null!, ui = null!, agent = null!, bind = null!;
            var page = Page("net.title", "net.sub", c =>
            {
                c.Text(Strings.T("net.names"), bold: true);
                names = c.Add(new TextBox { Text = string.Join(", ", _gateway.PublicNames), Multiline = true, ScrollBars = ScrollBars.Vertical }, 46);
                c.Gap(2);
                c.Text(Strings.T("net.namesHint"), muted: true);
                c.Gap(14);
                c.Text(Strings.T("net.uiPort"), bold: true);
                ui = c.Input(_gateway.UiPort.ToString(CultureInfo.InvariantCulture), 90);
                c.Text(Strings.T("net.uiPortHint"), muted: true);
                c.Gap(14);
                c.Text(Strings.T("net.agentPort"), bold: true);
                agent = c.Input(_gateway.AgentPort.ToString(CultureInfo.InvariantCulture), 90);
                c.Gap(14);
                c.Text(Strings.T("net.bind"), bold: true);
                bind = c.Input(_gateway.BindAddress, 160);
                c.Text(Strings.T("net.bindHint"), muted: true);
            });
            page.Leave = () =>
            {
                _gateway.PublicNames = SetupRules.SplitList(names.Text);
                _gateway.BindAddress = bind.Text.Trim();
                _gateway.UiPort = int.TryParse(ui.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : 0;
                _gateway.AgentPort = int.TryParse(agent.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0;
                var error = _gateway.Validate().FirstOrDefault(e => e == "err.names" || e == "err.ports" || e == "err.bind");
                if (error != null)
                {
                    return Strings.T(error);
                }
                foreach (var port in new[] { _gateway.UiPort, _gateway.AgentPort })
                {
                    if (!_ownPorts.Contains(port) && SetupEngine.PortBusy(port))
                    {
                        return Strings.T("err.portBusy", port);
                    }
                }
                return null;
            };
            return page;
        }

        private WizardPage CertificatePage()
        {
            RadioButton internalCert = null!, fromStore = null!, fromPfx = null!;
            ComboBox store = null!;
            TextBox file = null!, password = null!;
            var choices = SetupEngine.ServerCertificates();
            var current = SetupRules.NormalizeThumbprint(_gateway.UiCertificateThumbprint);
            var page = Page("cert.title", "cert.sub", c =>
            {
                internalCert = c.Radio(Strings.T("cert.internal"), current == null);
                c.Gap(10);
                fromStore = c.Radio(Strings.T("cert.store"), current != null);
                store = c.Add(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 23, 22);
                if (choices.Count == 0)
                {
                    store.Items.Add(Strings.T("cert.none"));
                    store.SelectedIndex = 0;
                    fromStore.Enabled = false;
                }
                else
                {
                    store.Items.AddRange(choices.Cast<object>().ToArray());
                    store.SelectedIndex = Math.Max(0, choices.FindIndex(x => string.Equals(x.Thumbprint, current, StringComparison.OrdinalIgnoreCase)));
                }
                c.Gap(12);
                fromPfx = c.Radio(Strings.T("cert.pfx"), false);
                file = c.Add(new TextBox(), 23, 22, 470);
                var browse = new Button { Text = Strings.T("cert.browse"), Location = new Point(500, file.Top - 1), Size = new Size(96, 25) };
                c.Panel.Controls.Add(browse);
                browse.Click += (_, __) =>
                {
                    using (var dialog = new OpenFileDialog { Filter = "PFX (*.pfx;*.p12)|*.pfx;*.p12", CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            file.Text = dialog.FileName;
                            fromPfx.Checked = true;
                        }
                    }
                };
                c.Gap(6);
                c.Text(Strings.T("cert.password"), muted: true, indent: 22);
                password = c.Input("", 240, password: true, indent: 22);
                c.Gap(14);
                c.Text(Strings.T("cert.hint"), muted: true);
                store.Enter += (_, __) => { if (fromStore.Enabled) fromStore.Checked = true; };
                file.Enter += (_, __) => fromPfx.Checked = true;
                password.Enter += (_, __) => fromPfx.Checked = true;
            });
            page.Leave = () =>
            {
                if (internalCert.Checked)
                {
                    _gateway.UiCertificateThumbprint = null;
                    return null;
                }
                if (fromStore.Checked)
                {
                    if (!(store.SelectedItem is SetupEngine.CertificateChoice choice))
                    {
                        return Strings.T("cert.choose");
                    }
                    _gateway.UiCertificateThumbprint = choice.Thumbprint;
                    return null;
                }
                if (file.Text.Trim().Length == 0 || !File.Exists(file.Text.Trim()))
                {
                    return Strings.T("cert.choosePfx");
                }
                try
                {
                    // Imported now, so that a wrong password is reported here and not in the middle of the installation.
                    _gateway.UiCertificateThumbprint = SetupEngine.ImportPfx(file.Text.Trim(), password.Text);
                    password.Text = "";
                    return null;
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    return Strings.T("cert.importFailed", ex.Message);
                }
            };
            return page;
        }

        private WizardPage FirewallPage()
        {
            CheckBox open = null!, updates = null!;
            TextBox ui = null!, agents = null!;
            var page = Page("fw.title", "fw.sub", c =>
            {
                open = c.Check(Strings.T(_upgrade ? "fw.openUpgrade" : "fw.open"), _gateway.OpenFirewall);
                c.Gap(12);
                c.Text(Strings.T("fw.ui"), bold: true, indent: 22);
                ui = c.Input(string.Join(", ", _gateway.UiRemoteAddress), indent: 22);
                c.Gap(10);
                c.Text(Strings.T("fw.agents"), bold: true, indent: 22);
                agents = c.Input(string.Join(", ", _gateway.AgentRemoteAddress), indent: 22);
                c.Gap(6);
                c.Text(Strings.T("fw.hint"), muted: true, indent: 22);
                c.Gap(16);
                c.Text(Strings.T("fw.skip"), muted: true);
                c.Gap(14);
                updates = c.Check(Strings.T("gw.updates"), !_gateway.DisableRemoteUpdate);
                c.Text(Strings.T("gw.updatesHelp"), muted: true, indent: 22);
                void Sync() { ui.Enabled = open.Checked; agents.Enabled = open.Checked; }
                open.CheckedChanged += (_, __) => Sync();
                Sync();
            });
            page.Leave = () =>
            {
                _gateway.OpenFirewall = open.Checked;
                _gateway.DisableRemoteUpdate = !updates.Checked;
                _gateway.UiRemoteAddress = SetupRules.SplitList(ui.Text);
                _gateway.AgentRemoteAddress = SetupRules.SplitList(agents.Text);
                return _gateway.Validate().Contains("err.firewall") ? Strings.T("err.firewall") : null;
            };
            return page;
        }

        // ------------------------------------------------------------------ pages: agent

        private WizardPage ModePage()
        {
            RadioButton host = null!, management = null!;
            var page = Page("mode.title", "mode.sub", c =>
            {
                host = c.Radio(Strings.T("mode.host"), !_agent.Management);
                c.Text(Strings.T("mode.hostHelp"), muted: true, indent: 22);
                c.Gap(16);
                management = c.Radio(Strings.T("mode.management"), _agent.Management);
                c.Text(Strings.T("mode.managementHelp"), muted: true, indent: 22);
            });
            // The choice may have been changed by the data pasted from the portal on the next page.
            page.Enter = () =>
            {
                management.Checked = _agent.Management;
                host.Checked = !_agent.Management;
            };
            page.Leave = () =>
            {
                _agent.Management = management.Checked;
                return !_agent.Management && !SetupEngine.HyperVPresent() ? Strings.T("mode.noHyperV") : null;
            };
            return page;
        }

        private WizardPage EnrollPage()
        {
            RadioButton? keep = null, again = null;
            TextBox gateway = null!, ca = null!, token = null!;
            Button paste = null!;
            var page = Page("enroll.title", "enroll.sub", c =>
            {
                if (_agentEnrolled)
                {
                    keep = c.Radio(Strings.T("enroll.keep"), true);
                    again = c.Radio(Strings.T("enroll.again"), false);
                    c.Gap(10);
                }
                paste = c.Add(new Button { Text = Strings.T("enroll.paste") }, 27, 0, 170);
                c.Gap(4);
                c.Text(Strings.T("enroll.pasteHelp"), muted: true);
                c.Gap(12);
                c.Text(Strings.T("enroll.gateway"), bold: true);
                gateway = c.Input(_agent.Gateway);
                c.Gap(8);
                c.Text(Strings.T("enroll.ca"), bold: true);
                ca = c.Input(_agent.CaSha256);
                ca.Font = new Font("Consolas", 9f);
                c.Gap(8);
                c.Text(Strings.T("enroll.token"), bold: true);
                token = c.Input(_agent.Token);
                token.Font = new Font("Consolas", 9f);
                void Sync()
                {
                    var on = again == null || again.Checked;
                    paste.Enabled = gateway.Enabled = ca.Enabled = token.Enabled = on;
                }
                if (again != null) again.CheckedChanged += (_, __) => Sync();
                Sync();
                paste.Click += (_, __) =>
                {
                    string text;
                    try
                    {
                        text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        text = "";
                    }
                    if (PortalData.TryParse(text, out var parsed))
                    {
                        gateway.Text = parsed.Gateway;
                        ca.Text = parsed.CaSha256;
                        token.Text = parsed.Token;
                        // A node of a failover cluster: the portal asks for the access of the agent to the cluster.
                        _agent.ClusterAccess = parsed.ClusterAccess;
                        if (parsed.Management != _agent.Management)
                        {
                            // The portal created these data for the other kind of agent: follow it, and say so.
                            _agent.Management = parsed.Management;
                            MessageBox.Show(this,
                                Strings.T("enroll.roleChanged", Strings.T(parsed.Management ? "mode.management" : "mode.host"), Strings.T(parsed.Management ? "mode.host" : "mode.management")),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    else
                    {
                        MessageBox.Show(this, Strings.T("enroll.pasteFailed"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                };
            });
            page.Leave = () =>
            {
                _agent.Enroll = again == null || again.Checked;
                _agent.ForceReenroll = _agentEnrolled && _agent.Enroll;
                _agent.Gateway = gateway.Text.Trim();
                _agent.CaSha256 = ca.Text.Trim();
                _agent.Token = token.Text.Trim();
                var error = _agent.Validate().FirstOrDefault(e => e != "err.account");
                if (error != null)
                {
                    return Strings.T(error);
                }
                // The pasted data may have switched the setup to "Hyper-V host" after the first page was checked.
                return !_agent.Management && !SetupEngine.HyperVPresent() ? Strings.T("mode.noHyperV") : null;
            };
            return page;
        }

        private WizardPage AccountPage()
        {
            RadioButton automatic = null!, named = null!;
            TextBox name = null!;
            var page = Page("acct.title", "acct.sub", c =>
            {
                automatic = c.Radio(Strings.T(_keepAccount ? "acct.keep" : "acct.virtual"), true);
                if (!_keepAccount) c.Text(Strings.T("acct.virtualHelp"), muted: true, indent: 22);
                c.Gap(16);
                named = c.Radio(Strings.T("acct.named"), false);
                name = c.Input("svc-hvportal", 220, indent: 22);
                c.Text(Strings.T("acct.namedHelp"), muted: true, indent: 22);
                name.Enter += (_, __) => named.Checked = true;
            });
            page.Leave = () =>
            {
                _agent.ServiceAccount = named.Checked ? name.Text.Trim() : null;
                return _agent.Validate().Contains("err.account") ? Strings.T("err.account") : null;
            };
            return page;
        }

        private WizardPage StoragePage()
        {
            RadioButton? keep = null;
            RadioButton all = null!, restrict = null!;
            TextBox folders = null!;
            CheckBox updates = null!;
            CheckBox cluster = null!;
            var page = Page("storage.title", "storage.sub", c =>
            {
                if (_upgrade)
                {
                    keep = c.Radio(Strings.T("storage.keep"), true);
                }
                all = c.Radio(Strings.T("storage.all"), !_upgrade);
                restrict = c.Radio(Strings.T("storage.restrict"), false);
                folders = c.Add(new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Text = "D:\\VMs" + Environment.NewLine + "D:\\ISO" }, 70, 22);
                c.Gap(4);
                c.Text(Strings.T("storage.help"), muted: true, indent: 22);
                c.Gap(14);
                updates = c.Check(Strings.T("storage.updates"), true);
                c.Text(Strings.T("storage.updatesHelp"), muted: true, indent: 22);
                c.Gap(14);
                // Node of a failover cluster (ADR-041): checked when the data from the portal ask for it or the Cluster service runs.
                cluster = c.Check(Strings.T("storage.cluster"), _agent.ClusterAccess == "Full" || (_agent.ClusterAccess == null && SetupEngine.ClusterServiceRunning()));
                c.Text(Strings.T("storage.clusterHelp"), muted: true, indent: 22);
                // The list (pre-filled with an example) is editable only when "only these folders" is chosen, so it is
                // never mistaken for a value that applies with the other choices.
                void Sync() => folders.Enabled = restrict.Checked;
                restrict.CheckedChanged += (_, __) => Sync();
                Sync();
            });
            page.Leave = () =>
            {
                _agent.StorageFolders = keep != null && keep.Checked ? null
                    : restrict.Checked ? SetupRules.SplitFolders(folders.Text)
                    : _upgrade ? new List<string>() : null;
                _agent.DisableRemoteUpdate = !updates.Checked;
                _agent.ClusterAccess = _agent.Management ? null : cluster.Checked ? "Full" : null;
                if (restrict.Checked && (_agent.StorageFolders == null || _agent.StorageFolders.Count == 0))
                {
                    return Strings.T("err.storage");
                }
                return _agent.Validate().Contains("err.storage") ? Strings.T("err.storage") : null;
            };
            return page;
        }

        // ------------------------------------------------------------------ summary and run

        private WizardPage SummaryPage()
        {
            Label text = null!;
            var page = Page("summary.title", "summary.sub", c => text = c.Add(new Label { UseMnemonic = false }, ContentHeight - 10));
            page.Enter = () => text.Text = string.Join(Environment.NewLine + Environment.NewLine, SummaryLines());
            return page;
        }

        private IEnumerable<string> SummaryLines()
        {
            if (SetupEngine.IsGateway)
            {
                yield return Strings.T("summary.names") + ":  " + string.Join(", ", _gateway.PublicNames);
                yield return Strings.T("summary.ports") + ":  " + Strings.T("summary.portsValue", _gateway.UiPort, _gateway.AgentPort, _gateway.BindAddress);
                yield return Strings.T("summary.cert") + ":  " + (_gateway.UiCertificateThumbprint ?? Strings.T("summary.certInternal"));
                yield return Strings.T("summary.firewall") + ":  " + (_gateway.OpenFirewall
                    ? Strings.T("summary.firewallValue", string.Join(", ", _gateway.UiRemoteAddress), string.Join(", ", _gateway.AgentRemoteAddress))
                    : Strings.T("summary.firewallNone"));
            }
            else
            {
                yield return Strings.T("summary.mode") + ":  " + Strings.T(_agent.Management ? "mode.management" : "mode.host");
                yield return Strings.T("summary.gateway") + ":  " + (_agent.Enroll ? SetupRules.NormalizeGatewayUrl(_agent.Gateway) : Strings.T("summary.keepEnroll"));
                yield return Strings.T("summary.account") + ":  " + (_agent.ServiceAccount ?? Strings.T(_keepAccount ? "summary.accountKeep" : "summary.accountVirtual"));
                yield return Strings.T("summary.storage") + ":  " + (_agent.StorageFolders == null
                    ? Strings.T(_upgrade ? "summary.storageKeep" : "summary.storageAll")
                    : _agent.StorageFolders.Count == 0 ? Strings.T("summary.storageAll") : string.Join(", ", _agent.StorageFolders));
                yield return Strings.T("summary.updates") + ":  " + Strings.T(_agent.DisableRemoteUpdate ? "summary.updatesOff" : "summary.updatesOn");
                if (!_agent.Management)
                {
                    yield return Strings.T("summary.cluster") + ":  " + Strings.T(_agent.ClusterAccess == "Full" ? "summary.clusterFull" : "summary.clusterNone");
                }
            }
            yield return SetupEngine.InstallPath;
        }

        private WizardPage RunPage() => Page("run.title", "run.sub", c =>
        {
            _status.UseMnemonic = false;
            c.Add(_status, 20);
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            c.Add(_progress, 10);
            c.Gap(8);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 8.5f);
            _log.BackColor = Color.White;
            _log.WordWrap = true;
            c.Add(_log, 200);
            c.Gap(8);
            c.Add(_result, ContentHeight - c.Y);
        });

        private void AppendLog(string line)
        {
            if (IsDisposed)
            {
                return;
            }
            BeginInvoke((Action)(() =>
            {
                _logLines.Add(line);
                _log.AppendText(line + Environment.NewLine);
            }));
        }

        private void StartInstallation()
        {
            _running = true;
            _cancel.Enabled = false;
            _status.Text = Strings.T("run.extract");
            var thread = new Thread(() =>
            {
                var code = -1;
                try
                {
                    _staging = SetupEngine.ExtractPayload();
                    BeginInvoke((Action)(() => _status.Text = Strings.T("run.start")));
                    var source = Path.Combine(_staging, SetupEngine.IsGateway ? "gateway" : "agent");
                    var parameters = SetupEngine.IsGateway ? _gateway.ToParameters(source) : _agent.ToParameters(source);
                    code = SetupEngine.RunInstaller(_staging, parameters, !SetupEngine.IsGateway && _agent.Enroll ? _agent.Token : null, AppendLog);
                }
                catch (Exception ex)
                {
                    AppendLog(ex.GetType().Name + ": " + ex.Message);
                }
                BeginInvoke((Action)(() => Finished(code == 0)));
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        /// <summary>Shows the outcome under the log: what to do next (gateway address and setup token) or that it failed.</summary>
        internal void Finished(bool ok, bool saveLog = true)
        {
            _running = false;
            _finished = true;
            _progress.Visible = false;
            _cancel.Enabled = true;
            _title.Text = Strings.T(ok ? "done.ok" : "done.fail");
            _subtitle.Text = "";
            _status.Text = "";
            var log = saveLog ? SetupEngine.SaveLog(_logLines) : null;
            var c = new Column(ContentWidth, _result.Height);
            c.Panel.Visible = true;
            if (!ok)
            {
                c.Text(Strings.T("done.failText"));
            }
            else if (SetupEngine.IsGateway)
            {
                var url = "https://" + _gateway.PublicNames.First() + (_gateway.UiPort == 443 ? "" : ":" + _gateway.UiPort.ToString(CultureInfo.InvariantCulture));
                var line = c.Text(Strings.T("done.gateway", url), bold: true);
                var open = new Button { Text = Strings.T("done.open"), Size = new Size(130, 25), Location = new Point(ContentWidth - 130, line.Top - 4) };
                open.Click += (_, __) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
                    {
                        MessageBox.Show(this, ex.Message, Text);
                    }
                };
                c.Panel.Controls.Add(open);
                var (token, file) = SetupEngine.GatewaySetupToken();
                if (token != null)
                {
                    c.Gap(8);
                    c.Text(Strings.T("done.token", file), muted: true);
                    var box = c.Add(new TextBox { Text = token, ReadOnly = true, Font = new Font("Consolas", 9f), BackColor = Color.White }, 23, 0, ContentWidth - 106);
                    var copy = new Button { Text = Strings.T("done.copy"), Size = new Size(96, 25), Location = new Point(ContentWidth - 96, box.Top - 1) };
                    copy.Click += (_, __) =>
                    {
                        try
                        {
                            Clipboard.SetText(token);
                        }
                        catch (System.Runtime.InteropServices.ExternalException)
                        {
                            // The token stays selectable in the box.
                        }
                    };
                    c.Panel.Controls.Add(copy);
                }
            }
            else
            {
                c.Text(Strings.T("done.agent"));
            }
            if (log != null)
            {
                c.Gap(6);
                c.Text(Strings.T("done.log", log), muted: true);
            }
            _result.Controls.Clear();
            _result.Controls.Add(c.Panel);
        }

        // ------------------------------------------------------------------ rendering (verification without installing)

        /// <summary>Saves a picture of every page (setup.exe --render &lt;folder&gt;): used to check the wizard without running it.</summary>
        public static void RenderAll(string folder)
        {
            Directory.CreateDirectory(folder);
            using (var wizard = new Wizard())
            {
                wizard.StartPosition = FormStartPosition.Manual;
                wizard.Location = new Point(-4000, -4000);
                wizard.ShowInTaskbar = false;
                wizard.Show();
                for (var i = 0; i < wizard._pages.Count; i++)
                {
                    wizard.Show(i);
                    if (i == wizard._pages.Count - 1)
                    {
                        wizard._log.Text = "Previous version backed up..." + Environment.NewLine + "HvPortal " + SetupEngine.Component + " is running.";
                        wizard.Finished(true, saveLog: false);
                    }
                    Save((i + 1).ToString(CultureInfo.InvariantCulture));
                }
                // The other outcome of the last page.
                wizard._log.Text = "TCP port 443 is already in use by System (PID 4). Free it or choose another port.";
                wizard.Finished(false, saveLog: false);
                Save("failed");
                wizard._finished = true;

                void Save(string name)
                {
                    Application.DoEvents();
                    using (var bitmap = new Bitmap(wizard.Width, wizard.Height))
                    {
                        wizard.DrawToBitmap(bitmap, new Rectangle(0, 0, wizard.Width, wizard.Height));
                        bitmap.Save(Path.Combine(folder, SetupEngine.Component.ToLowerInvariant() + "-" + name + (Strings.Italian ? "-it" : "-en") + ".png"));
                    }
                }
                wizard._index = 0;
                wizard.Close();
            }
        }
    }
}
