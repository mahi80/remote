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

        // Keys are "host:port" for direct connections and "relay:host:port" for relay
        // connections, so the two never invalidate each other.
        public static string Get(string key)
        {
            if (!File.Exists(FilePath)) return null;

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

        public static void Set(string key, string fingerprint)
        {
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

        public static string[] GetAll(string key)
        {
            List<string> found = new List<string>();
            if (!File.Exists(FilePath)) return found.ToArray();

            foreach (string line in File.ReadAllLines(FilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                int space = trimmed.IndexOf(' ');
                if (space <= 0) continue;
                if (!string.Equals(trimmed.Substring(0, space), key, StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(trimmed.Substring(space + 1).Trim());
            }
            return found.ToArray();
        }

        public static void Add(string key, string fingerprint)
        {
            foreach (string known in GetAll(key))
            {
                if (string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase)) return;
            }

            File.AppendAllText(FilePath, key + " " + fingerprint + Environment.NewLine);
        }
    }
}
