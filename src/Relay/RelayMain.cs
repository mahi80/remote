using System;
using System.Threading;
using Shared;

namespace Relay
{
    internal static class RelayMain
    {
        private static void Main(string[] args)
        {
            CliArgs cli = new CliArgs(args);
            Log.Init(cli.Get("log"));

            string bind = cli.Get("bind") ?? "0.0.0.0";
            int port = cli.GetInt("port", 5056);

            RelayServer server = new RelayServer(bind, port);
            server.Logged += delegate(string message)
            {
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " [relay] " + message);
                Log.Write("[relay] " + message);
            };

            try
            {
                server.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to start relay: " + ex.Message);
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine("RemoteDesktop relay listening on " + bind + ":" + port + ". Press Ctrl+C to stop.");

            ManualResetEventSlim stop = new ManualResetEventSlim();
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stop.Set();
            };

            stop.Wait();
            server.Dispose();
        }
    }
}
