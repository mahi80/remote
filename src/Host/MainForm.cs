using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;
using Shared;

namespace Host
{
    public sealed class MainForm : Form
    {
        private readonly string _bindAddress;
        private readonly int _port;
        private readonly string _certPath;
        private readonly float _scale;

        private readonly Label _passcodeLabel;
        private readonly Label _statusLabel;
        private readonly Label _addressLabel;
        private readonly TextBox _fingerprintBox;
        private readonly ListBox _logBox;
        private readonly Button _toggleButton;
        private readonly NotifyIcon _tray;

        private SessionServer _server;
        private string _passcode;
        private bool _running;
        private bool _exiting;

        public MainForm(string bindAddress, int port, string passcode, string certPath)
        {
            _bindAddress = bindAddress;
            _port = port;
            _certPath = certPath;
            _passcode = passcode;

            AutoScaleMode = AutoScaleMode.None;
            using (Graphics probe = CreateGraphics()) _scale = probe.DpiX / 96f;

            Text = "RemoteDesktop host";
            ClientSize = new Size(S(580), S(500));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            Font = new Font("Segoe UI", 9F);

            Label title = new Label();
            title.Text = "RemoteDesktop host";
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.Location = new Point(S(16), S(12));
            title.AutoSize = true;

            Label passHint = new Label();
            passHint.Text = "Passcode (read this to the person connecting):";
            passHint.Location = new Point(S(18), S(52));
            passHint.AutoSize = true;

            _passcodeLabel = new Label();
            _passcodeLabel.Text = _passcode;
            _passcodeLabel.Font = new Font("Consolas", 24F, FontStyle.Bold);
            _passcodeLabel.Location = new Point(S(14), S(70));
            _passcodeLabel.AutoSize = true;

            _addressLabel = new Label();
            _addressLabel.Text = DescribeAddress();
            _addressLabel.Location = new Point(S(18), S(124));
            _addressLabel.AutoSize = true;
            _addressLabel.ForeColor = Color.DimGray;

            _fingerprintBox = new TextBox();
            _fingerprintBox.ReadOnly = true;
            _fingerprintBox.BorderStyle = BorderStyle.None;
            _fingerprintBox.BackColor = SystemColors.Control;
            _fingerprintBox.Location = new Point(S(18), S(148));
            _fingerprintBox.Width = S(544);
            _fingerprintBox.TabStop = false;

            _statusLabel = new Label();
            _statusLabel.Text = "Starting...";
            _statusLabel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            _statusLabel.Location = new Point(S(18), S(174));
            _statusLabel.AutoSize = true;
            _statusLabel.ForeColor = Color.DimGray;

            _logBox = new ListBox();
            _logBox.Location = new Point(S(18), S(204));
            _logBox.Size = new Size(S(544), S(190));
            _logBox.HorizontalScrollbar = true;

            _toggleButton = new Button();
            _toggleButton.Text = "Stop";
            _toggleButton.Location = new Point(S(18), S(410));
            _toggleButton.Size = new Size(S(120), S(32));

            Button newPasscodeButton = new Button();
            newPasscodeButton.Text = "New passcode";
            newPasscodeButton.Location = new Point(S(148), S(410));
            newPasscodeButton.Size = new Size(S(130), S(32));

            Label hint = new Label();
            hint.Text = "Closing this window keeps the host listening in the tray. Every connection needs your approval.";
            hint.Location = new Point(S(18), S(454));
            hint.Size = new Size(S(544), S(34));
            hint.ForeColor = Color.DimGray;

            Controls.AddRange(new Control[]
            {
                title, passHint, _passcodeLabel, _addressLabel, _fingerprintBox,
                _statusLabel, _logBox, _toggleButton, newPasscodeButton, hint
            });

            _toggleButton.Click += delegate
            {
                if (_running) StopServer();
                else StartServer();
            };

            newPasscodeButton.Click += delegate
            {
                _passcode = Passcodes.New();
                _passcodeLabel.Text = _passcode;
                if (_server != null) _server.Passcode = _passcode;
                AppendLog("New passcode generated.");
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open host window", null, delegate { Restore(); });
            menu.Items.Add("Stop listening", null, delegate { StopServer(); });
            menu.Items.Add("Exit", null, delegate
            {
                _exiting = true;
                Close();
            });

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "RemoteDesktop host";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Restore(); };

            Shown += delegate { StartServer(); };
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private string DescribeAddress()
        {
            string address = _bindAddress == "0.0.0.0" ? LocalIPv4() : _bindAddress;
            return "Listening on " + address + ":" + _port;
        }

        private static string LocalIPv4()
        {
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        return address.ToString();
                }
            }
            catch (SocketException)
            {
            }
            return "this machine";
        }

        private void StartServer()
        {
            if (_running) return;

            try
            {
                X509Certificate2 certificate = Tls.LoadOrCreateHostCertificate(_certPath);
                _fingerprintBox.Text = "Certificate SHA-256: " + Tls.Fingerprint(certificate);

                _server = new SessionServer(_bindAddress, _port, _passcode, certificate);
                _server.Logged += AppendLog;
                _server.ConsentRequested += AskConsentAsync;
                _server.SessionStarted += OnSessionStarted;
                _server.SessionEnded += OnSessionEnded;
                _server.Start();

                _running = true;
                _toggleButton.Text = "Stop";
                SetStatus("Waiting for a connection", Color.DarkGreen);
            }
            catch (Exception ex)
            {
                AppendLog("Failed to start: " + ex.Message);
                SetStatus("Not listening: " + ex.Message, Color.Firebrick);
            }
        }

        private void StopServer()
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            _running = false;
            _toggleButton.Text = "Start";
            SetStatus("Not listening", Color.DimGray);
        }

        private Task<bool> AskConsentAsync(string remote)
        {
            if (IsDisposed) return Task.FromResult(false);

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Show();
                    Activate();

                    using (ConsentForm dialog = new ConsentForm(remote))
                    {
                        completion.SetResult(dialog.ShowDialog(this) == DialogResult.OK);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                completion.SetResult(false);
            }

            return completion.Task;
        }

        private void OnSessionStarted(string remote)
        {
            SetStatus("Being controlled by " + remote, Color.Firebrick);
        }

        private void OnSessionEnded()
        {
            SetStatus("Waiting for a connection", Color.DarkGreen);
        }

        private void AppendLog(string message)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), new object[] { message }); }
                catch (InvalidOperationException) { }
                return;
            }

            Log.Write("[host] " + message);
            _logBox.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (_logBox.Items.Count > 300) _logBox.Items.RemoveAt(0);
            _logBox.TopIndex = Math.Max(0, _logBox.Items.Count - 1);
        }

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, Color>(SetStatus), new object[] { text, color }); }
                catch (InvalidOperationException) { }
                return;
            }

            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private void Restore()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(2500, "Still listening",
                    "RemoteDesktop host is still running. Use the tray icon to reopen or exit.", ToolTipIcon.Info);
                return;
            }

            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }

            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
