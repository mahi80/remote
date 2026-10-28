using System;

namespace Shared
{
    public static class Endpoint
    {
        public static bool TryParse(string value, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(value)) return false;

            int colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1) return false;

            string candidateHost = value.Substring(0, colon).Trim();
            if (candidateHost.Length == 0) return false;

            int candidatePort;
            if (!int.TryParse(value.Substring(colon + 1).Trim(), out candidatePort)) return false;
            if (candidatePort < 1 || candidatePort > 65535) return false;

            host = candidateHost;
            port = candidatePort;
            return true;
        }
    }
}
