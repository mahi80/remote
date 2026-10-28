using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Host
{
    public sealed class SessionServer : IDisposable
    {
        private const int FrameIntervalMs = 100;

        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly ScreenCapture _capture = new ScreenCapture();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _acceptTask;
        private int _busy;
        private int _streamWidth = 1;
        private int _streamHeight = 1;
        private long _framesSent;

        public SessionServer(string bindAddress, int port, string passcode, X509Certificate2 certificate)
        {
            BindAddress = bindAddress;
            Port = port;
            Passcode = passcode;
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Parse(bindAddress), port);
        }

        public string BindAddress { get; private set; }
        public int Port { get; private set; }
        public string Passcode { get; set; }

        public event Action<string> Logged;
        public event Func<string, Task<bool>> ConsentRequested;
        public event Action<string> SessionStarted;
        public event Action SessionEnded;

        public void Start()
        {
            _listener.Start();
            _acceptTask = Task.Run(new Func<Task>(AcceptLoopAsync));
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            RaiseLog("Stopped listening.");
        }

        private async Task AcceptLoopAsync()
        {
            CancellationToken ct = _cts.Token;

            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException) { break; }

                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                {
                    RaiseLog("Rejected a second connection: one session at a time.");
                    client.Close();
                    continue;
                }

                TcpClient session = client;
#pragma warning disable 4014
                Task.Run(async delegate
                {
                    try
                    {
                        await HandleClientAsync(session, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RaiseLog("Session error: " + ex.Message);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                });
#pragma warning restore 4014
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken outer)
        {
            string remote = client.Client.RemoteEndPoint == null
                ? "unknown"
                : client.Client.RemoteEndPoint.ToString();

            using (client)
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(outer))
            using (SslStream ssl = Tls.CreateServerStream(client.GetStream()))
            {
                client.NoDelay = true;
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                await ssl.AuthenticateAsServerAsync(_certificate, false, Tls.Protocols, false).ConfigureAwait(false);

                Packet hello = await Protocol.ReadAsync(ssl, cts.Token).ConfigureAwait(false);
                bool authenticated = hello.Type == PacketType.Text && CheckPasscode(Protocol.ParseText(hello.Payload));
                if (!authenticated)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), CancellationToken.None).ConfigureAwait(false);
                    RaiseLog("Rejected unauthenticated connection from " + remote + ".");
                    return;
                }

                cts.CancelAfter(-1);

                Func<string, Task<bool>> consent = ConsentRequested;
                if (consent == null)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), cts.Token).ConfigureAwait(false);
                    return;
                }

                RaiseLog("Connection request from " + remote + " - waiting for approval.");
                bool allowed = await consent(remote).ConfigureAwait(false);
                if (!allowed)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), cts.Token).ConfigureAwait(false);
                    RaiseLog("Request from " + remote + " was denied.");
                    return;
                }

                await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("OK"), cts.Token).ConfigureAwait(false);
                RaiseLog("Session started with " + remote + ".");
                Action<string> started = SessionStarted;
                if (started != null) started(remote);

                try
                {
                    using (CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                    {
                        Task send = SendVideoAsync(ssl, session.Token);
                        Task receive = ReceiveInputAsync(ssl, session.Token);
                        await Task.WhenAny(send, receive).ConfigureAwait(false);
                        session.Cancel();
                        await Swallow(send).ConfigureAwait(false);
                        await Swallow(receive).ConfigureAwait(false);
                    }
                }
                finally
                {
                    RaiseLog("Session ended.");
                    Action ended = SessionEnded;
                    if (ended != null) ended();
                }
            }
        }

        private bool CheckPasscode(string text)
        {
            byte[] expected = Encoding.UTF8.GetBytes("AUTH " + Passcode);
            byte[] actual = Encoding.UTF8.GetBytes(text ?? "");
            if (expected.Length != actual.Length) return false;

            int difference = 0;
            for (int i = 0; i < expected.Length; i++)
                difference |= expected[i] ^ actual[i];

            return difference == 0;
        }

        private async Task SendVideoAsync(SslStream ssl, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                VideoFrame? frame = await Task.Run(new Func<VideoFrame?>(_capture.CaptureChanged), ct).ConfigureAwait(false);
                if (frame.HasValue)
                {
                    VideoFrame value = frame.Value;
                    _streamWidth = value.Width;
                    _streamHeight = value.Height;

                    await Protocol.WriteAsync(ssl, PacketType.Video, value.ToPayload(), ct).ConfigureAwait(false);

                    long sent = Interlocked.Increment(ref _framesSent);
                    if (sent % 100 == 0)
                        RaiseLog(string.Format("{0} frames sent ({1}x{2}).", sent, value.Width, value.Height));
                }

                await Task.Delay(FrameIntervalMs, ct).ConfigureAwait(false);
            }
        }

        private async Task ReceiveInputAsync(SslStream ssl, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Packet packet = await Protocol.ReadAsync(ssl, ct).ConfigureAwait(false);
                if (packet.Type != PacketType.Input) continue;

                InputEvent input = InputEvent.FromBytes(packet.Payload);
                try
                {
                    InputInjector.Apply(input, _streamWidth, _streamHeight);
                }
                catch (Exception ex)
                {
                    RaiseLog("Input error: " + ex.Message);
                }
            }
        }

        private static async Task Swallow(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseLog(string message)
        {
            Action<string> handler = Logged;
            if (handler != null) handler(message);
        }

        public void Dispose()
        {
            Stop();
            _capture.Dispose();
            _cts.Dispose();
        }
    }
}
