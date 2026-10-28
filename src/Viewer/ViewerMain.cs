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

            string target = cli.Get("connect");
            if (target == null)
            {
                Application.Run(new ConnectForm());
                return;
            }

            string host;
            int port;
            if (!ParseTarget(target, out host, out port))
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

        private static bool ParseTarget(string target, out string host, out int port)
        {
            host = null;
            port = 0;

            int colon = target.LastIndexOf(':');
            if (colon <= 0 || colon == target.Length - 1) return false;

            host = target.Substring(0, colon);
            return int.TryParse(target.Substring(colon + 1), out port);
        }
    }
}
