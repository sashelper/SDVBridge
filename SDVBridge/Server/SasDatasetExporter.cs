using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SAS.Shared.AddIns;
using SAS.Tasks.Toolkit;
using SAS.Tasks.Toolkit.Data;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal sealed class SasDatasetExporter
    {
        private readonly EgInteropContext _context;

        public SasDatasetExporter(EgInteropContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<DatasetExportResult> ExportAsync(DatasetOpenRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return _context.RunOnUiAsync(() => ExportInternal(request));
        }

        private DatasetExportResult ExportInternal(DatasetOpenRequest request)
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

            Log($"Resolving dataset {request.Libref}.{request.Member} on server '{request.Server ?? "<default>"}'...");
            var dataset = ResolveDataset(request, out var serverName);
            Log($"Resolved dataset. Server={serverName}, Libref={request.Libref}, Member={dataset.Member}");
            var exportFolder = CreateExportFolder(serverName, request.Libref);

            var desiredFileName = $"{dataset.Member}.sas7bdat";
            var desiredPath = Path.Combine(exportFolder, desiredFileName);
            if (File.Exists(desiredPath))
            {
                File.Delete(desiredPath);
            }

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

            Log($"Download complete. File created: {exportedFile}");

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

        private static string CreateExportFolder(string serverName, string libref)
        {
            var safeServer = SanitizePathSegment(string.IsNullOrWhiteSpace(serverName) ? "DefaultServer" : serverName);
            var safeLibref = SanitizePathSegment(string.IsNullOrWhiteSpace(libref) ? "UnknownLib" : libref);
            var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", safeServer, safeLibref);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string SanitizePathSegment(string input)
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
