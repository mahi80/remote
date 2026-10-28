using System;
using System.Collections.Generic;
using System.IO;

namespace Viewer
{
    public static class KnownHosts
    {
        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "known_hosts.txt"); }
        }

        public static string Get(string host, int port)
        {
            if (!File.Exists(FilePath)) return null;

            string key = host + ":" + port;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                int space = trimmed.IndexOf(' ');
                if (space <= 0) continue;
                if (!string.Equals(trimmed.Substring(0, space), key, StringComparison.OrdinalIgnoreCase)) continue;

                return trimmed.Substring(space + 1).Trim();
            }
            return null;
        }

        public static void Set(string host, int port, string fingerprint)
        {
            string key = host + ":" + port;
            List<string> lines = new List<string>();

            if (File.Exists(FilePath))
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)) continue;
                    lines.Add(trimmed);
                }
            }

            lines.Add(key + " " + fingerprint);
            File.WriteAllLines(FilePath, lines.ToArray());
        }
    }
}
