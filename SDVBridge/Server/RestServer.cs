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
        private readonly SasProgramService _programService;
        private readonly SasDatasetPreviewService _previewService;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;

        public RestServer(EgInteropContext context, int port)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _metadataService = new SasMetadataService(context);
            _exporter = new SasDatasetExporter(context);
            _programService = new SasProgramService(context);
            _previewService = new SasDatasetPreviewService(_metadataService, _programService);
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
                var rawPath = ctx.Request.Url.AbsolutePath.Trim('/');
                var path = rawPath.ToLowerInvariant();
                var rawSegments = string.IsNullOrEmpty(rawPath)
                    ? new string[0]
                    : rawPath.Split('/');

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
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 3 &&
                         string.Equals(rawSegments[0], "servers", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "libraries", StringComparison.OrdinalIgnoreCase))
                {
                    var serverName = WebUtility.UrlDecode(rawSegments[1]);
                    var libraries = await _metadataService.GetLibrariesAsync(serverName).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, libraries);
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 5 &&
                         string.Equals(rawSegments[0], "servers", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "libraries", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[4], "datasets", StringComparison.OrdinalIgnoreCase))
                {
                    var serverName = WebUtility.UrlDecode(rawSegments[1]);
                    var libref = WebUtility.UrlDecode(rawSegments[3]);
                    var datasets = await _metadataService.GetDatasetsAsync(serverName, libref).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, datasets);
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 7 &&
                         string.Equals(rawSegments[0], "servers", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "libraries", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[4], "datasets", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[6], "columns", StringComparison.OrdinalIgnoreCase))
                {
                    var serverName = WebUtility.UrlDecode(rawSegments[1]);
                    var libref = WebUtility.UrlDecode(rawSegments[3]);
                    var member = WebUtility.UrlDecode(rawSegments[5]);
                    var columns = await _metadataService.GetColumnsAsync(serverName, libref, member).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, columns);
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 7 &&
                         string.Equals(rawSegments[0], "servers", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "libraries", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[4], "datasets", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[6], "preview", StringComparison.OrdinalIgnoreCase))
                {
                    var serverName = WebUtility.UrlDecode(rawSegments[1]);
                    var libref = WebUtility.UrlDecode(rawSegments[3]);
                    var member = WebUtility.UrlDecode(rawSegments[5]);
                    var limit = ParseOptionalInt(ctx.Request.QueryString["limit"]);
                    var preview = await _previewService.GetPreviewAsync(serverName, libref, member, limit).ConfigureAwait(false);
                    WriteJson(ctx.Response, HttpStatusCode.OK, preview);
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
                else if (ctx.Request.HttpMethod == "POST" && path == "programs/submit")
                {
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    {
                        var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                        LogInfo($"/programs/submit request body: {payload}");

                        var request = JsonUtil.Deserialize<ProgramSubmitRequest>(payload ?? string.Empty);
                        if (request == null)
                        {
                            throw new InvalidOperationException("Request body is required.");
                        }

                        var result = await _programService.SubmitAsync(request).ConfigureAwait(false);
                        LogInfo($"/programs/submit response -> JobId={result.JobId}, Status={result.Status}");
                        WriteJson(ctx.Response, HttpStatusCode.OK, result);
                    }
                }
                else if (ctx.Request.HttpMethod == "POST" && path == "programs/submit/async")
                {
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    {
                        var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                        LogInfo($"/programs/submit/async request body: {payload}");

                        var request = JsonUtil.Deserialize<ProgramSubmitRequest>(payload ?? string.Empty);
                        if (request == null)
                        {
                            throw new InvalidOperationException("Request body is required.");
                        }

                        var result = _programService.QueueSubmit(request);
                        LogInfo($"/programs/submit/async response -> JobId={result.JobId}, Status={result.Status}");
                        WriteJson(ctx.Response, HttpStatusCode.Accepted, result);
                    }
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 2 &&
                         string.Equals(rawSegments[0], "jobs", StringComparison.OrdinalIgnoreCase))
                {
                    var jobId = WebUtility.UrlDecode(rawSegments[1]);
                    if (!_programService.TryGetStatus(jobId, out var statusResponse))
                    {
                        WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse($"Job '{jobId}' was not found."));
                    }
                    else
                    {
                        WriteJson(ctx.Response, HttpStatusCode.OK, statusResponse);
                    }
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 4 &&
                         string.Equals(rawSegments[0], "jobs", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    var jobId = WebUtility.UrlDecode(rawSegments[1]);
                    var artifactId = WebUtility.UrlDecode(rawSegments[3]);
                    if (!_programService.TryGetArtifact(jobId, artifactId, out var artifact))
                    {
                        WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse($"Artifact '{artifactId}' was not found for job '{jobId}'."));
                    }
                    else
                    {
                        WriteFile(
                            ctx.Response,
                            HttpStatusCode.OK,
                            artifact.Path,
                            artifact.ContentType,
                            artifact.Name);
                    }
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 3 &&
                         string.Equals(rawSegments[0], "jobs", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    var jobId = WebUtility.UrlDecode(rawSegments[1]);
                    if (!_programService.TryGetArtifacts(jobId, out var artifactsResponse))
                    {
                        WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse($"Job '{jobId}' was not found."));
                    }
                    else
                    {
                        WriteJson(ctx.Response, HttpStatusCode.OK, artifactsResponse);
                    }
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 3 &&
                         string.Equals(rawSegments[0], "jobs", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "log", StringComparison.OrdinalIgnoreCase))
                {
                    var jobId = WebUtility.UrlDecode(rawSegments[1]);
                    var offset = ParseOptionalInt(ctx.Request.QueryString["offset"]);
                    if (!_programService.TryGetLog(jobId, offset, out var logResponse))
                    {
                        WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse($"Job '{jobId}' was not found."));
                    }
                    else
                    {
                        WriteJson(ctx.Response, HttpStatusCode.OK, logResponse);
                    }
                }
                else if (ctx.Request.HttpMethod == "GET" &&
                         rawSegments.Length == 3 &&
                         string.Equals(rawSegments[0], "jobs", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(rawSegments[2], "output", StringComparison.OrdinalIgnoreCase))
                {
                    var jobId = WebUtility.UrlDecode(rawSegments[1]);
                    if (!_programService.TryGetOutput(jobId, out var outputResponse))
                    {
                        WriteJson(ctx.Response, HttpStatusCode.NotFound, new ErrorResponse($"Job '{jobId}' was not found."));
                    }
                    else
                    {
                        WriteJson(ctx.Response, HttpStatusCode.OK, outputResponse);
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

        private static int ParseOptionalInt(string text)
        {
            if (int.TryParse(text, out var value))
            {
                return value;
            }

            return 0;
        }

        private static void WriteFile(HttpListenerResponse response, HttpStatusCode statusCode, string filePath, string contentType, string fileName)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("Artifact file was not found.", filePath);
            }

            response.StatusCode = (int)statusCode;
            response.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
            ApplyCorsHeaders(response);

            var downloadName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(filePath) : fileName;
            response.AddHeader("Content-Disposition", $"attachment; filename=\"{EscapeHeaderFileName(downloadName)}\"");

            using (var stream = File.OpenRead(filePath))
            {
                response.ContentLength64 = stream.Length;
                stream.CopyTo(response.OutputStream);
            }
        }

        private static string EscapeHeaderFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "artifact.bin";
            }

            return fileName.Replace("\"", "_");
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
