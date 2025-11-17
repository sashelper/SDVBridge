using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal sealed class RestServer : IDisposable
    {
        private readonly EgInteropContext _context;
        private readonly SasMetadataService _metadataService;
        private readonly SasDatasetExporter _exporter;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;

        public RestServer(EgInteropContext context, int port)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _metadataService = new SasMetadataService(context);
            _exporter = new SasDatasetExporter(context);
            Port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        }

        public int Port { get; }

        public void Start()
        {
            if (_loop != null)
            {
                return;
            }

            _listener.Start();
            _loop = Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context), token);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch
                {
                    context?.Response?.Abort();
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();
                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    HandleCorsPreflight(ctx.Response);
                    ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                }
                else if (ctx.Request.HttpMethod == "GET" && path == "servers")
                {
                    var servers = await _metadataService.GetServersAsync().ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, servers);
                }
                else if (ctx.Request.HttpMethod == "GET" && path.StartsWith("servers/") && path.EndsWith("/libraries"))
                {
                    var segments = path.Split('/');
                    var serverName = WebUtility.UrlDecode(segments[1]);
                    var libraries = await _metadataService.GetLibrariesAsync(serverName).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, libraries);
                }
                else if (ctx.Request.HttpMethod == "GET" && path.Contains("/libraries/") && path.EndsWith("/datasets"))
                {
                    var segments = path.Split('/');
                    var serverName = WebUtility.UrlDecode(segments[1]);
                    var libref = WebUtility.UrlDecode(segments[3]);
                    var datasets = await _metadataService.GetDatasetsAsync(serverName, libref).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, datasets);
                }
                else if (ctx.Request.HttpMethod == "POST" && path == "datasets/open")
                {
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    {
                        var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                        LogInfo($"/datasets/open request body: {payload}");

                        var request = JsonUtil.Deserialize<DatasetOpenRequest>(payload ?? string.Empty);
                        if (request == null)
                        {
                            throw new InvalidOperationException("Request body is required.");
                        }

                        var result = await _exporter.ExportAsync(request).ConfigureAwait(false);
                        LogInfo($"/datasets/open success -> {result.LocalPath}");
                        WriteJson(ctx.Response, HttpStatusCode.OK, new DatasetOpenResponse
                        {
                            Path = result.LocalPath,
                            FileName = result.FileName
                        });
                    }
                }
                else
                {
                    WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse("Endpoint not found."));
                }
            }
            catch (Exception ex)
            {
                LogError("Request failed", ex);
                WriteJson(ctx.Response, HttpStatusCode.InternalServerError, new ErrorResponse(ex.Message));
            }
            finally
            {
                ctx.Response.OutputStream.Close();
            }
        }

        private static void WriteJson(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
        {
            var json = JsonUtil.Serialize(payload);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            ApplyCorsHeaders(response);
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }

        private static void HandleCorsPreflight(HttpListenerResponse response)
        {
            ApplyCorsHeaders(response);
            response.AddHeader("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        }

        private static void ApplyCorsHeaders(HttpListenerResponse response)
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");
        }

        private static void LogInfo(string message)
        {
            SDVBridgeLog.Info($"[RestServer] {message}");
        }

        private static void LogError(string message, Exception ex)
        {
            SDVBridgeLog.Error($"[RestServer] {message}", ex);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch { }
            _listener.Close();
        }
    }
}
