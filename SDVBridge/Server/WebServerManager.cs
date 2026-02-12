using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using SAS.Shared.AddIns;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal static class WebServerManager
    {
        private static readonly object SyncRoot = new object();
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SDVBridge",
            "settings.json");
        private static RestServer _server;
        private static int _port = 17832;
        private static string _defaultServerLogPath;
        private static string _defaultServerOutputPath;

        static WebServerManager()
        {
            LoadPersistedCapturePaths();
        }

        public static bool IsRunning => _server != null;

        public static int Port => _port;

        public static string DefaultServerLogPath
        {
            get
            {
                lock (SyncRoot)
                {
                    return _defaultServerLogPath;
                }
            }
        }

        public static string DefaultServerOutputPath
        {
            get
            {
                lock (SyncRoot)
                {
                    return _defaultServerOutputPath;
                }
            }
        }

        public static void SetDefaultCapturePaths(string serverLogPath, string serverOutputPath)
        {
            var normalizedLogPath = NormalizeOptionalPath(serverLogPath);
            var normalizedOutputPath = NormalizeOptionalPath(serverOutputPath);
            var hasLogPath = !string.IsNullOrWhiteSpace(normalizedLogPath);
            var hasOutputPath = !string.IsNullOrWhiteSpace(normalizedOutputPath);
            if (hasLogPath != hasOutputPath)
            {
                throw new ArgumentException("Server log path and server output path must be provided together.");
            }

            lock (SyncRoot)
            {
                _defaultServerLogPath = normalizedLogPath;
                _defaultServerOutputPath = normalizedOutputPath;
                PersistCapturePathsNoThrow();
            }
        }

        public static void Start(ISASTaskConsumer consumer, int requestedPort)
        {
            if (consumer == null) throw new ArgumentNullException(nameof(consumer));
            if (requestedPort <= 0 || requestedPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(requestedPort), "Port must be between 1 and 65535.");
            }

            lock (SyncRoot)
            {
                if (_server == null)
                {
                    _port = requestedPort;
                    var context = EgInteropContext.EnsureInitialized(consumer);
                    _server = new RestServer(context, _port);
                    _server.Start();
                }
                else if (_port != requestedPort)
                {
                    StopInternal();
                    var context = EgInteropContext.EnsureInitialized(consumer);
                    _port = requestedPort;
                    _server = new RestServer(context, _port);
                    _server.Start();
                }
            }
        }

        public static void Stop()
        {
            lock (SyncRoot)
            {
                StopInternal();
            }
        }

        private static void StopInternal()
        {
            _server?.Dispose();
            _server = null;
        }

        private static string NormalizeOptionalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return path.Trim();
        }

        private static void LoadPersistedCapturePaths()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return;
                }

                var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var settings = JsonUtil.Deserialize<CapturePathSettings>(json);
                var logPath = NormalizeOptionalPath(settings.ServerLogPath);
                var outputPath = NormalizeOptionalPath(settings.ServerOutputPath);
                var hasLogPath = !string.IsNullOrWhiteSpace(logPath);
                var hasOutputPath = !string.IsNullOrWhiteSpace(outputPath);
                if (hasLogPath != hasOutputPath)
                {
                    return;
                }

                lock (SyncRoot)
                {
                    _defaultServerLogPath = logPath;
                    _defaultServerOutputPath = outputPath;
                }
            }
            catch
            {
                // Ignore persisted settings loading failures.
            }
        }

        private static void PersistCapturePathsNoThrow()
        {
            try
            {
                var folder = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var payload = new CapturePathSettings
                {
                    ServerLogPath = _defaultServerLogPath,
                    ServerOutputPath = _defaultServerOutputPath
                };
                var json = JsonUtil.Serialize(payload);
                File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
            }
            catch
            {
                // Ignore persisted settings writing failures.
            }
        }

        [DataContract]
        private sealed class CapturePathSettings
        {
            [DataMember(Name = "serverlogpath", EmitDefaultValue = false)]
            public string ServerLogPath { get; set; }

            [DataMember(Name = "serveroutputpath", EmitDefaultValue = false)]
            public string ServerOutputPath { get; set; }
        }
    }
}
