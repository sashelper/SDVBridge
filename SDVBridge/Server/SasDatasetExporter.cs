using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SAS.Shared.AddIns;
using SAS.Tasks.Toolkit;
using SAS.Tasks.Toolkit.Data;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    /// <summary>
    /// Downloads a SAS data set to a local .sas7bdat file.
    ///
    /// SasData.DownloadToFolder transfers the whole data set synchronously. It used to run on
    /// Enterprise Guide's UI thread (RunOnUiAsync), which froze EG for as long as a large
    /// download took. Transfers now run on a dedicated STA worker thread (the pattern of the
    /// deferred output capture in SasProgramService) and only fall back to the UI thread when
    /// the EG object model refuses to be called from another thread.
    /// </summary>
    internal sealed class SasDatasetExporter
    {
        private const int RpcEWrongThread = unchecked((int)0x8001010E);

        /// <summary>One transfer at a time: the EG / IOM objects are not ours to hammer in parallel.</summary>
        internal static readonly SemaphoreSlim TransferGate = new SemaphoreSlim(1, 1);

        private readonly EgInteropContext _context;

        public SasDatasetExporter(EgInteropContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Download into the shared folder of the library (the synchronous endpoint).</summary>
        public Task<DatasetExportResult> ExportAsync(DatasetOpenRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var tcs = new TaskCompletionSource<DatasetExportResult>();
            StartWorker("SDVBridgeExport", () =>
            {
                TransferGate.Wait();
                try
                {
                    tcs.SetResult(Export(request, null, null));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    TransferGate.Release();
                }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Download on the calling thread into a folder of its own (<paramref name="jobId"/>), so
        /// concurrent or repeated downloads of one data set never overwrite a file in use.
        /// <paramref name="folderReady"/> receives that folder before the transfer starts (for
        /// progress polling). Call it on a worker thread, never on EG's UI thread.
        /// </summary>
        public DatasetExportResult Export(DatasetOpenRequest request, string jobId, Action<string> folderReady)
        {
            try
            {
                return ExportInternal(request, jobId, folderReady);
            }
            catch (Exception ex) when (IsWrongThread(ex))
            {
                Log($"EG refused a call from the worker thread ({ex.GetType().Name}: {ex.Message}); retrying on the UI thread (EG will be busy meanwhile).");
                return _context.RunOnUiAsync(() => ExportInternal(request, jobId, folderReady)).GetAwaiter().GetResult();
            }
        }

        public static void StartWorker(string name, Action body)
        {
            var worker = new Thread(() => body())
            {
                IsBackground = true,
                Name = name
            };
            // The EG / IOM COM objects expect a single-threaded apartment.
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private static bool IsWrongThread(Exception ex)
        {
            var com = ex as COMException;
            if (com != null && com.HResult == RpcEWrongThread)
            {
                return true;
            }
            // An STA object used from another apartment without a proxy.
            return ex is InvalidCastException && ex.Message.IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private DatasetExportResult ExportInternal(DatasetOpenRequest request, string jobId, Action<string> folderReady)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.Libref))
            {
                throw new ArgumentException("Libref is required.", nameof(request.Libref));
            }

            if (string.IsNullOrWhiteSpace(request.Member))
            {
                throw new ArgumentException("Dataset member name is required.", nameof(request.Member));
            }

            var started = DateTime.UtcNow;
            Log($"Resolving dataset {request.Libref}.{request.Member} on server '{request.Server ?? "<default>"}' (thread {Thread.CurrentThread.ManagedThreadId}, {Thread.CurrentThread.GetApartmentState()})...");
            var dataset = ResolveDataset(request, out var serverName);
            Log($"Resolved dataset. Server={serverName}, Libref={request.Libref}, Member={dataset.Member}");
            var exportFolder = CreateExportFolder(serverName, request.Libref);
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                exportFolder = Path.Combine(exportFolder, "jobs", SanitizePathSegment(jobId));
                Directory.CreateDirectory(exportFolder);
            }

            var desiredFileName = $"{dataset.Member}.sas7bdat";
            var desiredPath = Path.Combine(exportFolder, desiredFileName);
            if (File.Exists(desiredPath))
            {
                File.Delete(desiredPath);
            }

            folderReady?.Invoke(exportFolder);
            Log($"Downloading dataset to {exportFolder}...");
            dataset.DownloadToFolder(exportFolder);

            var exportedFile = Directory
                .GetFiles(exportFolder, "*.sas7bdat", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), desiredFileName, StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetFiles(exportFolder, "*.sas7bdat", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exportedFile == null)
            {
                throw new InvalidOperationException("Dataset download completed but no file was produced.");
            }

            Log($"Download complete in {(DateTime.UtcNow - started).TotalSeconds:F1}s. File created: {exportedFile}");

            if (!string.Equals(exportedFile, desiredPath, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Renaming {exportedFile} to {desiredPath} to preserve member name.");
                if (File.Exists(desiredPath))
                {
                    File.Delete(desiredPath);
                }
                File.Move(exportedFile, desiredPath);
                exportedFile = desiredPath;
            }

            Log($"Returning dataset file: {exportedFile}");
            return new DatasetExportResult
            {
                FileName = Path.GetFileName(exportedFile),
                LocalPath = exportedFile,
                ContentType = "application/octet-stream"
            };
        }

        private SasData ResolveDataset(DatasetOpenRequest request, out string resolvedServerName)
        {
            var serverName = string.IsNullOrWhiteSpace(request.Server)
                ? _context.Consumer?.AssignedServer
                : request.Server;

            var server = FindServer(serverName);
            resolvedServerName = server.Name;
            var library = server
                .GetSasLibraries()
                .FirstOrDefault(l => string.Equals(l.Libref, request.Libref, StringComparison.OrdinalIgnoreCase));

            if (library == null)
            {
                throw new InvalidOperationException($"Library '{request.Libref}' was not found on server '{server.Name}'.");
            }

            if (!library.IsAssigned)
            {
                library.Assign();
            }

            var dataset = library
                .GetSasDataMembers()
                .FirstOrDefault(d => string.Equals(d.Member, request.Member, StringComparison.OrdinalIgnoreCase));

            if (dataset == null)
            {
                throw new InvalidOperationException($"Dataset '{request.Member}' was not found in '{request.Libref}'.");
            }

            return dataset;
        }

        private static SasServer FindServer(string requestedServer)
        {
            var servers = SasServer.GetSasServers();
            if (servers == null || !servers.Any())
            {
                throw new InvalidOperationException("No SAS servers are available.");
            }

            if (string.IsNullOrWhiteSpace(requestedServer))
            {
                return servers.First();
            }

            var match = servers.FirstOrDefault(s => string.Equals(s.Name, requestedServer, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new InvalidOperationException($"Server '{requestedServer}' was not found.");
            }

            return match;
        }

        internal static string CreateExportFolder(string serverName, string libref)
        {
            var safeServer = SanitizePathSegment(string.IsNullOrWhiteSpace(serverName) ? "DefaultServer" : serverName);
            var safeLibref = SanitizePathSegment(string.IsNullOrWhiteSpace(libref) ? "UnknownLib" : libref);
            var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", safeServer, safeLibref);
            Directory.CreateDirectory(folder);
            return folder;
        }

        internal static string SanitizePathSegment(string input)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var buffer = input
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray();
            return new string(buffer);
        }

        private static void Log(string message)
        {
            SDVBridgeLog.Info($"[SasDatasetExporter] {message}");
        }
    }
}
