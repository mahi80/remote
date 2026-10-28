using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shared
{
    public static class Tls
    {
        public const SslProtocols Protocols = SslProtocols.Tls12;

        public static X509Certificate2 LoadOrCreateHostCertificate(string path)
        {
            if (File.Exists(path))
                return new X509Certificate2(File.ReadAllBytes(path), (string)null, X509KeyStorageFlags.Exportable);

            byte[] pkcs12;
            using (RSA rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                CertificateRequest request = new CertificateRequest(
                    "CN=RemoteDesktopHost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)))
                {
                    pkcs12 = certificate.Export(X509ContentType.Pkcs12);
                }
            }

            File.WriteAllBytes(path, pkcs12);
            return new X509Certificate2(pkcs12, (string)null, X509KeyStorageFlags.Exportable);
        }

        public static string Fingerprint(X509Certificate certificate)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", "");
            }
        }

        // Accepts any certificate during the handshake; Client.ConnectAsync pins the fingerprint immediately after,
        // before the passcode is sent.
        public static SslStream CreateClientStream(Stream inner)
        {
            return new SslStream(inner, false, delegate { return true; }, null);
        }

        public static SslStream CreateServerStream(Stream inner)
        {
            return new SslStream(inner, false);
        }
    }
}
