using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace Viewer
{
    public static class Client
    {
        public static async Task<SslStream> ConnectAsync(
            string host, int port, string passcode, bool trustNewHost,
            Func<string, bool> confirmFingerprint, CancellationToken ct)
        {
            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;

            try
            {
                await tcp.ConnectAsync(host, port).ConfigureAwait(false);
                return await AuthenticateAsync(tcp.GetStream(), host + ":" + port, host, false,
                    passcode, trustNewHost, confirmFingerprint, ct).ConfigureAwait(false);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        public static async Task<SslStream> ConnectViaRelayAsync(
            string relayHost, int relayPort, string code, string passcode, bool trustNewHost,
            Func<string, bool> confirmFingerprint, CancellationToken ct)
        {
            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;

            try
            {
                await tcp.ConnectAsync(relayHost, relayPort).ConfigureAwait(false);

                using (CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));

                    Stream raw = tcp.GetStream();
                    await LineProtocol.WriteLineAsync(raw, "VIEW " + code, handshake.Token).ConfigureAwait(false);
                    string reply = await LineProtocol.ReadLineAsync(raw, handshake.Token).ConfigureAwait(false);

                    if (reply == null) throw new IOException("The relay closed the connection.");
                    if (!reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(RelayMessage(reply));
                }

                // One relay can front several hosts, so relay pinning accepts any
                // fingerprint previously seen behind that relay, not just one.
                return await AuthenticateAsync(tcp.GetStream(), "relay:" + relayHost + ":" + relayPort, relayHost, true,
                    passcode, trustNewHost, confirmFingerprint, ct).ConfigureAwait(false);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        private static async Task<SslStream> AuthenticateAsync(
            Stream raw, string pinKey, string tlsTarget, bool allowMultipleFingerprints,
            string passcode, bool trustNewHost, Func<string, bool> confirmFingerprint, CancellationToken ct)
        {
            SslStream ssl = Tls.CreateClientStream(raw);
            try
            {
                await ssl.AuthenticateAsClientAsync(tlsTarget, null, Tls.Protocols, false).ConfigureAwait(false);

                if (ssl.RemoteCertificate == null)
                    throw new IOException("Host presented no certificate.");

                string fingerprint = Tls.Fingerprint(ssl.RemoteCertificate);
                VerifyPin(pinKey, fingerprint, allowMultipleFingerprints, trustNewHost, confirmFingerprint);

                await Protocol.WriteAsync(ssl, PacketType.Text, Protocol.TextPacket("AUTH " + passcode), ct)
                    .ConfigureAwait(false);
                Packet reply = await Protocol.ReadAsync(ssl, ct).ConfigureAwait(false);
                if (reply.Type != PacketType.Text || Protocol.ParseText(reply.Payload) != "OK")
                    throw new InvalidOperationException("Host rejected the connection (wrong passcode or request declined).");

                return ssl;
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
        }

        private static void VerifyPin(
            string pinKey, string fingerprint, bool allowMultiple, bool trustNew,
            Func<string, bool> confirmFingerprint)
        {
            if (!allowMultiple)
            {
                string known = KnownHosts.Get(pinKey);
                if (known == null)
                {
                    bool trusted = trustNew || (confirmFingerprint != null && confirmFingerprint(fingerprint));
                    if (!trusted)
                        throw new InvalidOperationException(
                            "Unknown host certificate.\n\nSHA-256: " + fingerprint +
                            "\n\nConnect from the window to review it, or pass --trust-new.");
                    KnownHosts.Set(pinKey, fingerprint);
                }
                else if (!string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Host certificate changed for " + pinKey +
                        ".\n\nExpected " + known + "\nGot      " + fingerprint);
                }
                return;
            }

            string[] knownRelay = KnownHosts.GetAll(pinKey);
            for (int i = 0; i < knownRelay.Length; i++)
            {
                if (string.Equals(knownRelay[i], fingerprint, StringComparison.OrdinalIgnoreCase)) return;
            }

            bool accepted = trustNew || (confirmFingerprint != null && confirmFingerprint(fingerprint));
            if (!accepted)
                throw new InvalidOperationException(
                    "Unknown host certificate behind the relay.\n\nSHA-256: " + fingerprint +
                    "\n\nCompare it with the fingerprint shown on the host, or pass --trust-new.");

            KnownHosts.Add(pinKey, fingerprint);
        }

        private static string RelayMessage(string reply)
        {
            if (reply.StartsWith("ERR NOHOST", StringComparison.OrdinalIgnoreCase))
                return "No host is sharing with that session code.";
            if (reply.StartsWith("ERR BUSY", StringComparison.OrdinalIgnoreCase))
                return "That host already has an active viewer.";
            if (reply.StartsWith("ERR THROTTLE", StringComparison.OrdinalIgnoreCase))
                return "Too many attempts from this address. Wait a minute and try again.";
            if (reply.StartsWith("ERR INUSE", StringComparison.OrdinalIgnoreCase))
                return "That session code is already registered on the relay.";
            return "The relay rejected the request: " + reply;
        }
    }
}
