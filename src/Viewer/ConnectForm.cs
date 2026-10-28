using System;
using System.Drawing;
using System.Net.Security;
using System.Threading;
using System.Windows.Forms;
using Shared;

namespace Viewer
{
    public sealed class ConnectForm : Form
    {
        private readonly TextBox _hostBox;
        private readonly NumericUpDown _portBox;
        private readonly TextBox _passcodeBox;
        private readonly Button _connectButton;
        private readonly Label _statusLabel;
        private readonly float _scale;

        public ConnectForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics probe = CreateGraphics()) _scale = probe.DpiX / 96f;

            Text = "RemoteDesktop - connect";
            ClientSize = new Size(S(440), S(296));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            Font = new Font("Segoe UI", 9F);

            Label hostLabel = new Label();
            hostLabel.Text = "Host or IP address";
            hostLabel.Location = new Point(S(18), S(16));
            hostLabel.AutoSize = true;

            _hostBox = new TextBox();
            _hostBox.Location = new Point(S(18), S(38));
            _hostBox.Width = S(400);
            _hostBox.Text = "127.0.0.1";

            Label portLabel = new Label();
            portLabel.Text = "Port";
            portLabel.Location = new Point(S(18), S(74));
            portLabel.AutoSize = true;

            _portBox = new NumericUpDown();
            _portBox.Location = new Point(S(18), S(96));
            _portBox.Width = S(110);
            _portBox.Minimum = 1;
            _portBox.Maximum = 65535;
            _portBox.Value = 5055;

            Label passLabel = new Label();
            passLabel.Text = "Passcode shown on the host";
            passLabel.Location = new Point(S(18), S(132));
            passLabel.AutoSize = true;

            _passcodeBox = new TextBox();
            _passcodeBox.Location = new Point(S(18), S(154));
            _passcodeBox.Width = S(400);

            _connectButton = new Button();
            _connectButton.Text = "Connect";
            _connectButton.Location = new Point(S(18), S(198));
            _connectButton.Size = new Size(S(120), S(32));

            _statusLabel = new Label();
            _statusLabel.Location = new Point(S(18), S(240));
            _statusLabel.Size = new Size(S(404), S(44));
            _statusLabel.ForeColor = Color.DimGray;

            AcceptButton = _connectButton;
            _connectButton.Click += OnConnect;

            Controls.AddRange(new Control[] { hostLabel, _hostBox, portLabel, _portBox, passLabel, _passcodeBox, _connectButton, _statusLabel });

            Shown += delegate
            {
                _hostBox.Focus();
                _hostBox.SelectAll();
            };
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private async void OnConnect(object sender, EventArgs e)
        {
            string host = _hostBox.Text.Trim();
            int port = (int)_portBox.Value;
            string passcode = _passcodeBox.Text;

            if (host.Length == 0)
            {
                _statusLabel.Text = "Enter a host name or IP address.";
                return;
            }

            _connectButton.Enabled = false;
            _statusLabel.Text = "Connecting...";

            try
            {
                SslStream stream = await Client.ConnectAsync(host, port, passcode, false, ConfirmFingerprint, CancellationToken.None);
                Hide();

                RemoteForm remote = new RemoteForm(stream, host + ":" + port);
                remote.FormClosed += delegate { Close(); };
                remote.Show();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = ex.Message.Replace("\r", " ").Replace("\n", " ");
                _connectButton.Enabled = true;
            }
        }

        private bool ConfirmFingerprint(string fingerprint)
        {
            if (InvokeRequired)
                return (bool)Invoke(new Func<string, bool>(ConfirmFingerprint), new object[] { fingerprint });

            string message = "This host has not been seen before.\n\nSHA-256 fingerprint:\n" +
                             fingerprint + "\n\nTrust this host?";
            return MessageBox.Show(this, message, "Unknown host",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }
}
