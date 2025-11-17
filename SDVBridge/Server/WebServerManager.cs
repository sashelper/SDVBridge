using System;
using SAS.Shared.AddIns;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal static class WebServerManager
    {
        private static readonly object SyncRoot = new object();
        private static RestServer _server;
        private static int _port = 17832;

        public static bool IsRunning => _server != null;

        public static int Port => _port;

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
    }
}
