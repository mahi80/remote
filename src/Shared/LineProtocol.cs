using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shared
{
    // Newline-terminated ASCII handshake lines used before a relay tunnel starts.
    // Reads one byte at a time so no TLS bytes are consumed along with the line.
    public static class LineProtocol
    {
        public const int MaxLineLength = 128;

        public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
            if (bytes.Length > MaxLineLength) throw new InvalidDataException("Handshake line too long.");

            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            StringBuilder line = new StringBuilder(32);
            byte[] one = new byte[1];

            while (true)
            {
                int read = await stream.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                if (read == 0)
                    return line.Length == 0 ? null : line.ToString();

                if (one[0] == (byte)'\n')
                    return line.ToString().TrimEnd('\r');

                if (line.Length >= MaxLineLength)
                    throw new InvalidDataException("Handshake line too long.");

                line.Append((char)one[0]);
            }
        }
    }
}
