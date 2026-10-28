using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Host;
using Relay;
using Shared;
using Viewer;

namespace Smoke
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static async Task<int> Run()
        {
            string knownHosts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "known_hosts.txt");
            if (File.Exists(knownHosts)) File.Delete(knownHosts);

            string workDir = Path.Combine(Path.GetTempPath(), "rd-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workDir);

            int failures = 0;
            X509Certificate2Holder certificate = new X509Certificate2Holder(Tls.LoadOrCreateHostCertificate(Path.Combine(workDir, "host.pfx")));
            int port = FreePort();
            string passcode = "482913";
            int consentRequests = 0;

            SessionServer server = new SessionServer("127.0.0.1", port, passcode, certificate.Value);
            server.ConsentRequested += delegate(string remote)
            {
                Interlocked.Increment(ref consentRequests);
                Console.WriteLine("ok  host asked for consent from " + remote);
                return Task.FromResult(true);
            };
            server.Logged += delegate(string message) { Console.WriteLine("[host] " + message); };
            server.Start();

            try
            {
                try
                {
                    using (SslStream rejected = await Client.ConnectAsync("127.0.0.1", port, "000000", true, null, CancellationToken.None))
                    {
                        Console.WriteLine("FAIL wrong passcode was accepted");
                        failures++;
                    }
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine("ok  wrong passcode rejected");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL unexpected error for wrong passcode: " + ex.GetType().Name + " " + ex.Message);
                    failures++;
                }

                using (SslStream ssl = await Client.ConnectAsync("127.0.0.1", port, passcode, true, null, CancellationToken.None))
                {
                    int width = 0;
                    int height = 0;
                    int frames = 0;

                    using (CancellationTokenSource timeout = new CancellationTokenSource(20000))
                    {
                        while (frames < 3)
                        {
                            Packet packet = await Protocol.ReadAsync(ssl, timeout.Token);
                            if (packet.Type != PacketType.Video) continue;

                            VideoFrame frame = VideoFrame.FromPayload(packet.Payload);
                            width = frame.Width;
                            height = frame.Height;

                            using (MemoryStream buffer = new MemoryStream(frame.Jpeg))
                            using (Image image = Image.FromStream(buffer))
                            {
                                if (image.Width != width || image.Height != height)
                                {
                                    Console.WriteLine("FAIL frame header/image mismatch");
                                    failures++;
                                }
                            }

                            frames++;
                        }
                    }

                    Console.WriteLine(string.Format("ok  received {0} frames at {1}x{2}, jpeg decodes", frames, width, height));

                    Rectangle screen = SystemInformation.VirtualScreen;
                    Point before = Cursor.Position;
                    int streamX = (int)Math.Round((before.X - screen.Left) * (width - 1) / (double)Math.Max(1, screen.Width - 1));
                    int streamY = (int)Math.Round((before.Y - screen.Top) * (height - 1) / (double)Math.Max(1, screen.Height - 1));

                    InputEvent move = new InputEvent(InputKind.MouseMove, streamX, streamY, 0);
                    await Protocol.WriteAsync(ssl, PacketType.Input, move.ToBytes(), CancellationToken.None);
                    await Task.Delay(700);

                    Point after = Cursor.Position;
                    if (Math.Abs(after.X - before.X) <= 3 && Math.Abs(after.Y - before.Y) <= 3)
                    {
                        Console.WriteLine(string.Format("ok  mouse round-trip kept position ({0},{1})", before.X, before.Y));
                    }
                    else
                    {
                        Console.WriteLine(string.Format("FAIL mouse moved from ({0},{1}) to ({2},{3})", before.X, before.Y, after.X, after.Y));
                        failures++;
                    }
                }

                // Relay mode: the host dials out, the viewer pairs by session code,
                // and the existing TLS/passcode/consent flow runs through the tunnel.
                int relayPort = FreePort();
                string relayCode = SessionCodes.New();
                int relayConsent = 0;

                using (RelayServer relay = new RelayServer("127.0.0.1", relayPort))
                {
                    relay.Logged += delegate(string message) { Console.WriteLine("[relay] " + message); };
                    relay.Start();

                    using (RelayHost relayHost = new RelayHost("127.0.0.1", relayPort, relayCode, passcode, certificate.Value))
                    {
                        relayHost.ConsentRequested += delegate(string remote)
                        {
                            Interlocked.Increment(ref relayConsent);
                            Console.WriteLine("ok  relay host asked for consent from " + remote);
                            return Task.FromResult(true);
                        };
                        relayHost.Logged += delegate(string message) { Console.WriteLine("[relay host] " + message); };
                        relayHost.Start();

                        using (SslStream relayed = await ConnectViaRelayWithRetry("127.0.0.1", relayPort, relayCode, passcode))
                        {
                            int frames = 0;
                            int width = 0;
                            int height = 0;

                            using (CancellationTokenSource timeout = new CancellationTokenSource(20000))
                            {
                                while (frames < 2)
                                {
                                    Packet packet = await Protocol.ReadAsync(relayed, timeout.Token);
                                    if (packet.Type != PacketType.Video) continue;

                                    VideoFrame frame = VideoFrame.FromPayload(packet.Payload);
                                    width = frame.Width;
                                    height = frame.Height;
                                    frames++;
                                }
                            }

                            Console.WriteLine(string.Format(
                                "ok  received {0} frames through relay at {1}x{2}", frames, width, height));

                            Rectangle screen = SystemInformation.VirtualScreen;
                            Point before = Cursor.Position;
                            int streamX = (int)Math.Round((before.X - screen.Left) * (width - 1) / (double)Math.Max(1, screen.Width - 1));
                            int streamY = (int)Math.Round((before.Y - screen.Top) * (height - 1) / (double)Math.Max(1, screen.Height - 1));

                            InputEvent move = new InputEvent(InputKind.MouseMove, streamX, streamY, 0);
                            await Protocol.WriteAsync(relayed, PacketType.Input, move.ToBytes(), CancellationToken.None);
                            await Task.Delay(700);

                            Point after = Cursor.Position;
                            if (Math.Abs(after.X - before.X) <= 3 && Math.Abs(after.Y - before.Y) <= 3)
                            {
                                Console.WriteLine(string.Format(
                                    "ok  relayed mouse round-trip kept position ({0},{1})", before.X, before.Y));
                            }
                            else
                            {
                                Console.WriteLine(string.Format(
                                    "FAIL relayed mouse moved from ({0},{1}) to ({2},{3})", before.X, before.Y, after.X, after.Y));
                                failures++;
                            }
                        }

                        try
                        {
                            using (SslStream rejected = await ConnectViaRelayWithRetry("127.0.0.1", relayPort, relayCode, "000000"))
                            {
                                Console.WriteLine("FAIL relayed wrong passcode was accepted");
                                failures++;
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            if (ex.Message.IndexOf("Host rejected", StringComparison.OrdinalIgnoreCase) >= 0)
                                Console.WriteLine("ok  relayed wrong passcode rejected");
                            else
                            {
                                Console.WriteLine("FAIL unexpected relayed error: " + ex.Message);
                                failures++;
                            }
                        }

                        if (relayConsent != 1)
                        {
                            Console.WriteLine("FAIL expected one relay consent request, saw " + relayConsent);
                            failures++;
                        }
                    }
                }

                if (consentRequests != 1)
                {
                    Console.WriteLine("FAIL expected exactly one consent request, saw " + consentRequests);
                    failures++;
                }
            }
            finally
            {
                server.Dispose();
                certificate.Value.Dispose();
                try { Directory.Delete(workDir, true); } catch (Exception) { }
            }

            Console.WriteLine(failures == 0 ? "PASS" : "FAILURES: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private sealed class X509Certificate2Holder
        {
            public readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Value;
            public X509Certificate2Holder(System.Security.Cryptography.X509Certificates.X509Certificate2 value) { Value = value; }
        }

        private static async Task<SslStream> ConnectViaRelayWithRetry(string host, int port, string code, string passcode)
        {
            Exception last = null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    return await Client.ConnectViaRelayAsync(host, port, code, passcode, true, null, CancellationToken.None);
                }
                catch (InvalidOperationException ex)
                {
                    last = ex;
                    string message = ex.Message;
                    bool transient =
                        message.IndexOf("No host is sharing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        message.IndexOf("already registered", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        message.IndexOf("active viewer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        message.IndexOf("Too many attempts", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!transient) throw;
                }
                catch (SocketException ex)
                {
                    last = ex;
                }

                await Task.Delay(250);
            }

            if (last != null) throw last;
            throw new InvalidOperationException("The relay host did not register in time.");
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
