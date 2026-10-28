using System;
using System.IO;

namespace Shared
{
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _path = Path.GetFullPath(path);
            File.WriteAllText(_path, "");
        }

        public static void Write(string message)
        {
            string path = _path;
            if (path == null) return;
            lock (Gate)
            {
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
    }
}
