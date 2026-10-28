using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Host
{
    // Runs one authenticated, consented session over any byte stream: a direct
    // socket or a relay tunnel. The caller owns the underlying stream.
    public sealed class SessionHandler : IDisposable
    {
        private const int FrameIntervalMs = 100;

        private readonly X509Certificate2 _certificate;
        private readonly ScreenCapture _capture = new ScreenCapture();
        private int _streamWidth = 1;
        private int _streamHeight = 1;

        public SessionHandler(string passcode, X509Certificate2 certificate)
        {
            Passcode = passcode;
            _certificate = certificate;
        }

        public string Passcode { get; set; }

        public event Action<string> Logged;
        public event Func<string, Task<bool>> ConsentRequested;
        public event Action<string> SessionStarted;
        public event Action SessionEnded;

        internal void WriteLog(string message)
        {
            Action<string> handler = Logged;
            if (handler != null) handler(message);
        }

        public async Task HandleAsync(Stream raw, string remote, CancellationToken outer)
        {
            _streamWidth = 1;
            _streamHeight = 1;

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(outer))
            using (SslStream ssl = Tls.CreateServerStream(raw))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                await ssl.AuthenticateAsServerAsync(_certificate, false, Tls.Protocols, false).ConfigureAwait(false);

                Packet hello = await Protocol.ReadAsync(ssl, cts.Token).ConfigureAwait(false);
                bool authenticated = hello.Type == PacketType.Text && CheckPasscode(Protocol.ParseText(hello.Payload));
                if (!authenticated)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), CancellationToken.None).ConfigureAwait(false);
                    WriteLog("Rejected unauthenticated connection from " + remote + ".");
                    return;
                }

                cts.CancelAfter(-1);

                Func<string, Task<bool>> consent = ConsentRequested;
                if (consent == null)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), cts.Token).ConfigureAwait(false);
                    return;
                }

                WriteLog("Connection request from " + remote + " - waiting for approval.");
                bool allowed = await consent(remote).ConfigureAwait(false);
                if (!allowed)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("DENY"), cts.Token).ConfigureAwait(false);
                    WriteLog("Request from " + remote + " was denied.");
                    return;
                }

                await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("OK"), cts.Token).ConfigureAwait(false);
                WriteLog("Session started with " + remote + ".");
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
                    WriteLog("Session ended.");
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
                    WriteLog("Input error: " + ex.Message);
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

        public void Dispose()
        {
            _capture.Dispose();
        }
    }
}
