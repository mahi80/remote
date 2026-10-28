using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Shared
{
    public sealed class CliArgs
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CliArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

                string value = "1";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];

                _values[arg.Substring(2)] = value;
            }
        }

        public bool Has(string name)
        {
            return _values.ContainsKey(name);
        }

        public string Get(string name)
        {
            string value;
            return _values.TryGetValue(name, out value) ? value : null;
        }

        public int GetInt(string name, int fallback)
        {
            int value;
            string raw = Get(name);
            return raw != null && int.TryParse(raw, out value) ? value : fallback;
        }
    }

    public static class Passcodes
    {
        public static string New()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return (BitConverter.ToUInt32(bytes, 0) % 1000000).ToString("D6");
        }
    }
}
