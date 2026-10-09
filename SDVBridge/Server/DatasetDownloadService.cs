using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace SDVBridge.Server
{
    /// <summary>
    /// Data set downloads as jobs the client polls, so a large download neither freezes
    /// Enterprise Guide nor blocks the client:
    ///   POST /datasets/open/async        -> { jobid, status: "queued" }
    ///   GET  /datasets/jobs/{id}         -> status, bytes written so far, path when completed
    ///   POST /datasets/jobs/{id}/cancel  -> status "cancelled"
    /// SasData.DownloadToFolder cannot be interrupted: "cancel" releases the client at once,
    /// and the file is thrown away when the transfer ends.
    /// </summary>
    internal sealed class DatasetDownloadService
    {
        private const int ProgressIntervalMs = 500;
        private static readonly TimeSpan JobRetention = TimeSpan.FromHours(6);

        private readonly SasDatasetExporter _exporter;
        private readonly ConcurrentDictionary<string, DownloadJob> _jobs =
            new ConcurrentDictionary<string, DownloadJob>(StringComparer.OrdinalIgnoreCase);

        public DatasetDownloadService(SasDatasetExporter exporter)
        {
            _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        }

        public DatasetJobResponse Queue(DatasetOpenRequest request)
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

            PruneOldJobs();
            var job = new DownloadJob
            {
                JobId = Guid.NewGuid().ToString("N"),
                Request = request,
                Status = DownloadStatus.Queued,
                CreatedUtc = DateTime.UtcNow
            };
            _jobs[job.JobId] = job;
            Log($"Queued download job {job.JobId} for {request.Libref}.{request.Member} on '{request.Server ?? "<default>"}'.");
            SasDatasetExporter.StartWorker("SDVBridgeDownload_" + job.JobId, () => Run(job));
            return job.ToResponse();
        }

        public bool TryGetStatus(string jobId, out DatasetJobResponse response)
        {
            response = null;
            DownloadJob job;
            if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out job))
            {
                return false;
            }
            response = job.ToResponse();
            return true;
        }

        public bool TryCancel(string jobId, out DatasetJobResponse response)
        {
            response = null;
            DownloadJob job;
            if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out job))
            {
                return false;
            }
            lock (job)
            {
                job.CancelRequested = true;
                if (job.Status == DownloadStatus.Queued || job.Status == DownloadStatus.Running)
                {
                    job.Status = DownloadStatus.Cancelled;
                    job.CompletedUtc = DateTime.UtcNow;
                }
            }
            Log($"Cancel requested for download job {jobId}.");
            response = job.ToResponse();
            return true;
        }

        private void Run(DownloadJob job)
        {
            SasDatasetExporter.TransferGate.Wait();
            Timer progressTimer = null;
            try
            {
                lock (job)
                {
                    if (job.CancelRequested)
                    {
                        Log($"Download job {job.JobId} was cancelled before it started.");
                        return;
                    }
                    job.Status = DownloadStatus.Running;
                    job.StartedUtc = DateTime.UtcNow;
                }

                var result = _exporter.Export(job.Request, job.JobId, folder =>
                {
                    job.Folder = folder;
                    progressTimer = new Timer(_ => Interlocked.Exchange(ref job.BytesDone, FolderBytes(folder)),
                                              null, ProgressIntervalMs, ProgressIntervalMs);
                });
                progressTimer?.Dispose();
                progressTimer = null;

                lock (job)
                {
                    Interlocked.Exchange(ref job.BytesDone, SafeLength(result.LocalPath));
                    if (job.CancelRequested)
                    {
                        Log($"Download job {job.JobId} finished after it was cancelled; discarding {result.LocalPath}.");
                        TryDeleteFolder(job.Folder);
                        return;
                    }
                    job.Path = result.LocalPath;
                    job.FileName = result.FileName;
                    job.Status = DownloadStatus.Completed;
                    job.CompletedUtc = DateTime.UtcNow;
                }
                Log($"Download job {job.JobId} completed: {result.LocalPath} ({job.BytesDone} bytes).");
                RemoveOlderCopies(job);
            }
            catch (Exception ex)
            {
                progressTimer?.Dispose();
                lock (job)
                {
                    if (job.CancelRequested)
                    {
                        TryDeleteFolder(job.Folder);
                        return;
                    }
                    job.Status = DownloadStatus.Failed;
                    job.Error = ex.Message;
                    job.CompletedUtc = DateTime.UtcNow;
                }
                TryDeleteFolder(job.Folder);
                SDVBridgeLog.Error($"[DatasetDownloadService] Download job {job.JobId} failed", ex);
            }
            finally
            {
                SasDatasetExporter.TransferGate.Release();
            }
        }

        // Each job downloads into jobs/<jobId>; once a newer copy of the same data set is
        // complete, the older ones go (best effort: a file still open is left alone).
        private void RemoveOlderCopies(DownloadJob finished)
        {
            try
            {
                var jobsRoot = Path.GetDirectoryName(finished.Folder);
                if (string.IsNullOrWhiteSpace(jobsRoot) || !Directory.Exists(jobsRoot))
                {
                    return;
                }
                foreach (var dir in Directory.GetDirectories(jobsRoot))
                {
                    if (string.Equals(dir, finished.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var sameMember = File.Exists(Path.Combine(dir, finished.FileName ?? string.Empty));
                    var runningJob = _jobs.Values.Any(j => string.Equals(j.Folder, dir, StringComparison.OrdinalIgnoreCase)
                                                           && (j.Status == DownloadStatus.Running || j.Status == DownloadStatus.Queued));
                    if (sameMember && !runningJob)
                    {
                        TryDeleteFolder(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Cleanup of older downloads failed: {ex.Message}");
            }
        }

        private void PruneOldJobs()
        {
            var cutoff = DateTime.UtcNow - JobRetention;
            foreach (var pair in _jobs.ToArray())
            {
                var job = pair.Value;
                if (job.CompletedUtc.HasValue && job.CompletedUtc.Value < cutoff)
                {
                    DownloadJob removed;
                    _jobs.TryRemove(pair.Key, out removed);
                }
            }
        }

        private static long FolderBytes(string folder)
        {
            try
            {
                return Directory.GetFiles(folder).Sum(SafeLength);
            }
            catch
            {
                return 0;
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? 0 : new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static void TryDeleteFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex)
            {
                Log($"Could not delete {folder}: {ex.Message}");
            }
        }

        private static void Log(string message)
        {
            SDVBridgeLog.Info($"[DatasetDownloadService] {message}");
        }

        private static class DownloadStatus
        {
            public const string Queued = "queued";
            public const string Running = "running";
            public const string Completed = "completed";
            public const string Failed = "failed";
            public const string Cancelled = "cancelled";
        }

        private sealed class DownloadJob
        {
            public string JobId;
            public DatasetOpenRequest Request;
            public volatile string Status;
            public volatile bool CancelRequested;
            public string Folder;
            public string Path;
            public string FileName;
            public string Error;
            public long BytesDone;
            public DateTime CreatedUtc;
            public DateTime? StartedUtc;
            public DateTime? CompletedUtc;

            public DatasetJobResponse ToResponse()
            {
                lock (this)
                {
                    var end = CompletedUtc ?? DateTime.UtcNow;
                    return new DatasetJobResponse
                    {
                        JobId = JobId,
                        Status = Status,
                        Server = Request?.Server,
                        Libref = Request?.Libref,
                        Member = Request?.Member,
                        BytesDone = Interlocked.Read(ref BytesDone),
                        ElapsedMs = StartedUtc.HasValue ? (long)(end - StartedUtc.Value).TotalMilliseconds : 0,
                        Path = Path,
                        FileName = FileName,
                        Error = Error
                    };
                }
            }
        }
    }
}
