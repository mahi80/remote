using System;
using System.Drawing;
using System.Windows.Forms;

namespace Host
{
    public sealed class ConsentForm : Form
    {
        private readonly Timer _timer = new Timer();
        private readonly Label _countdown;
        private readonly float _scale;
        private int _seconds = 30;

        public ConsentForm(string remote)
        {
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics probe = CreateGraphics()) _scale = probe.DpiX / 96f;

            Text = "Remote control request";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(S(560), S(200));
            Icon = SystemIcons.Application;
            Font = new Font("Segoe UI", 9F);

            Label title = new Label();
            title.Text = "Allow " + remote + " to control this computer?";
            title.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            title.Location = new Point(S(18), S(16));
            title.Size = new Size(S(524), S(30));

            Label body = new Label();
            body.Text = "They will see this screen and be able to use the mouse and keyboard.\r\nOnly allow someone you trust.";
            body.Location = new Point(S(18), S(52));
            body.Size = new Size(S(524), S(48));

            Button allow = new Button();
            allow.Text = "Allow";
            allow.Location = new Point(S(300), S(140));
            allow.Size = new Size(S(110), S(34));
            allow.DialogResult = DialogResult.OK;

            Button deny = new Button();
            deny.Text = "Deny";
            deny.Location = new Point(S(422), S(140));
            deny.Size = new Size(S(110), S(34));
            deny.DialogResult = DialogResult.Cancel;

            _countdown = new Label();
            _countdown.Text = "Auto-deny in 30s";
            _countdown.Location = new Point(S(18), S(148));
            _countdown.AutoSize = true;
            _countdown.ForeColor = Color.DimGray;

            AcceptButton = allow;
            CancelButton = deny;
            Controls.AddRange(new Control[] { title, body, allow, deny, _countdown });

            _timer.Interval = 1000;
            _timer.Tick += delegate
            {
                _seconds--;
                _countdown.Text = "Auto-deny in " + _seconds + "s";
                if (_seconds > 0) return;
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Shown += delegate
            {
                Activate();
                _timer.Start();
            };
            FormClosed += delegate { _timer.Stop(); };
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }
    }
}
