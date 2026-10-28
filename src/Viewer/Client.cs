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

                SslStream ssl = Tls.CreateClientStream(tcp.GetStream());
                try
                {
                    await ssl.AuthenticateAsClientAsync(host, null, Tls.Protocols, false).ConfigureAwait(false);

                    if (ssl.RemoteCertificate == null)
                        throw new IOException("Host presented no certificate.");

                    string fingerprint = Tls.Fingerprint(ssl.RemoteCertificate);
                    string known = KnownHosts.Get(host, port);

                    if (known == null)
                    {
                        bool trusted = trustNewHost || (confirmFingerprint != null && confirmFingerprint(fingerprint));
                        if (!trusted)
                            throw new InvalidOperationException(
                                "Unknown host certificate.\n\nSHA-256: " + fingerprint +
                                "\n\nConnect from the window to review it, or pass --trust-new.");
                        KnownHosts.Set(host, port, fingerprint);
                    }
                    else if (!string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Host certificate changed for " + host + ":" + port +
                            ".\n\nExpected " + known + "\nGot      " + fingerprint);
                    }

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
            catch
            {
                tcp.Close();
                throw;
            }
        }
    }
}
