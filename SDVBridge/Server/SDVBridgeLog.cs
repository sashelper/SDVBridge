using System;
using System.IO;
using System.Text;

namespace SDVBridge.Server
{
    internal static class SDVBridgeLog
    {
        private static readonly object SyncRoot = new object();
        private static readonly Lazy<string> LogFilePath = new Lazy<string>(CreateLogFilePath);

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception exception)
        {
            var formatted = exception == null ? message : $"{message}: {exception}";
            Write("ERROR", formatted);
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var line = $"{timestamp} [{level}] {message}";
                var path = LogFilePath.Value;
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                lock (SyncRoot)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }

                System.Diagnostics.Debug.WriteLine($"[SDVBridge] {line}");
            }
            catch
            {
                // Logging failures should not bubble up into the add-in runtime.
            }
        }

        private static string CreateLogFilePath()
        {
            var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", "Logs");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "SDVBridge.log");
        }
    }
}
