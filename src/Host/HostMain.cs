using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Shared;

namespace Host
{
    internal static class HostMain
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            CliArgs cli = new CliArgs(args);
            Log.Init(cli.Get("log"));
            SetProcessDPIAware();

            if (cli.Has("inject-test"))
            {
                bool ok = InputInjector.SelfTest();
                Log.Write("inject self-test: " + (ok ? "ok" : "failed"));
                Environment.ExitCode = ok ? 0 : 1;
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string bind = cli.Get("bind") ?? "0.0.0.0";
            int port = cli.GetInt("port", 5055);
            string passcode = cli.Get("passcode") ?? Passcodes.New();
            string certPath = cli.Get("cert") ?? Path.Combine(AppContext.BaseDirectory, "host.pfx");

            string relay = cli.Get("relay");
            string relayHost = null;
            int relayPort = 0;
            if (relay != null && !Endpoint.TryParse(relay, out relayHost, out relayPort))
            {
                MessageBox.Show("Use --relay host:port (for example --relay relay.example.com:5056).",
                    "RemoteDesktop host");
                return;
            }

            string code = cli.Get("code");
            if (relayHost != null)
            {
                if (code == null)
                {
                    code = SessionCodes.New();
                }
                else if (!SessionCodes.Valid(code))
                {
                    MessageBox.Show("--code must be 6 to 12 digits.", "RemoteDesktop host");
                    return;
                }
            }

            Application.Run(new MainForm(bind, port, passcode, code, certPath, relayHost, relayPort));
        }
    }
}
