using System;
using System.Collections.Generic;
using System.IO;

namespace LinkPilot.Core
{
    /// <summary>Tiny thread-safe file log at %APPDATA%\LinkPilot\log.txt (rolls over at ~1 MB).</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static readonly Queue<string> Recent = new Queue<string>();
        private const long MaxBytes = 1024 * 1024;

        public static string FilePath => Path.Combine(AppSettings.Folder, "log.txt");

        public static void Write(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
            lock (Gate)
            {
                Recent.Enqueue(line);
                while (Recent.Count > 200) Recent.Dequeue();
                try
                {
                    Directory.CreateDirectory(AppSettings.Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch
                {
                    // logging must never take the app down
                }
            }
        }

        public static string[] RecentLines()
        {
            lock (Gate) return Recent.ToArray();
        }
    }
}
