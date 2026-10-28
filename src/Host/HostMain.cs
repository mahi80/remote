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

            Application.Run(new MainForm(bind, port, passcode, certPath));
        }
    }
}
