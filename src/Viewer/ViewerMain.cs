using System;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Shared;

namespace Viewer
{
    internal static class ViewerMain
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            CliArgs cli = new CliArgs(args);
            Log.Init(cli.Get("log"));
            SetProcessDPIAware();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string relay = cli.Get("relay");
            if (relay != null)
            {
                ConnectViaRelay(relay, cli);
                return;
            }

            string target = cli.Get("connect");
            if (target == null)
            {
                Application.Run(new ConnectForm());
                return;
            }

            string host;
            int port;
            if (!Endpoint.TryParse(target, out host, out port))
            {
                MessageBox.Show("Use --connect host:port", "RemoteDesktop viewer");
                return;
            }

            string passcode = cli.Get("passcode") ?? "";

            try
            {
                SslStream stream = Client.ConnectAsync(host, port, passcode, cli.Has("trust-new"), null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Log.Write("connected to " + host + ":" + port + " (waiting for frames)");
                Application.Run(new RemoteForm(stream, host + ":" + port));
            }
            catch (Exception ex)
            {
                Log.Write("connect failed: " + ex.Message);
                MessageBox.Show(ex.Message, "RemoteDesktop viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void ConnectViaRelay(string relay, CliArgs cli)
        {
            string relayHost;
            int relayPort;
            if (!Endpoint.TryParse(relay, out relayHost, out relayPort))
            {
                MessageBox.Show("Use --relay host:port", "RemoteDesktop viewer");
                return;
            }

            string code = cli.Get("code");
            if (!SessionCodes.Valid(code))
            {
                MessageBox.Show("Relay mode needs --code <session code>.", "RemoteDesktop viewer");
                return;
            }

            string passcode = cli.Get("passcode") ?? "";
            string target = relayHost + ":" + relayPort;

            try
            {
                SslStream stream = Client.ConnectViaRelayAsync(relayHost, relayPort, code, passcode,
                        cli.Has("trust-new"), null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Log.Write("connected to relay " + target + " (waiting for frames)");
                Application.Run(new RemoteForm(stream, target));
            }
            catch (Exception ex)
            {
                Log.Write("connect failed: " + ex.Message);
                MessageBox.Show(ex.Message, "RemoteDesktop viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
