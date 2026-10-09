using System.Collections.Concurrent;
using System.Diagnostics;
using MockServer.Models;

namespace MockServer;

/// <summary>
/// Mock of SDVBridge's asynchronous data set download (POST /datasets/open/async,
/// GET /datasets/jobs/{id}, POST /datasets/jobs/{id}/cancel). The "transfer" writes the
/// export of MockDataStore.CreateDataset (or MOCK_SAS7BDAT, a real file, when set) in
/// chunks over MOCK_DOWNLOAD_SECONDS seconds (default 6), so clients can see progress and
/// cancel. Like the real bridge, a cancelled transfer runs to its end and is then discarded.
/// </summary>
internal sealed class MockDownloads
{
    private readonly MockDataStore _store;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public MockDownloads(MockDataStore store) => _store = store;

    public DatasetJobResponse Queue(DatasetOpenRequest request)
    {
        // Validates the data set (throws InvalidOperationException) and gives the bytes.
        var export = _store.CreateDataset(request);
        var source = Environment.GetEnvironmentVariable("MOCK_SAS7BDAT");
        var bytes = !string.IsNullOrWhiteSpace(source) && File.Exists(source)
            ? File.ReadAllBytes(source)
            : File.ReadAllBytes(export.LocalPath);
        var job = new Job
        {
            JobId = Guid.NewGuid().ToString("N"),
            Request = request,
            Status = "queued",
        };
        _jobs[job.JobId] = job;
        _ = Task.Run(() => RunAsync(job, bytes, export.FileName));
        return job.ToResponse();
    }

    public bool TryGet(string jobId, out DatasetJobResponse response)
    {
        response = null!;
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        response = job.ToResponse();
        return true;
    }

    public bool TryCancel(string jobId, out DatasetJobResponse response)
    {
        response = null!;
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        lock (job)
        {
            job.CancelRequested = true;
            if (job.Status is "queued" or "running") job.Status = "cancelled";
        }
        response = job.ToResponse();
        return true;
    }

    private static async Task RunAsync(Job job, byte[] bytes, string fileName)
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("MOCK_DOWNLOAD_SECONDS"), out var s) ? s : 6.0;
        var folder = Path.Combine(Path.GetTempPath(), "SDVBridgeMock", "jobs", job.JobId);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, fileName);
        lock (job)
        {
            if (job.CancelRequested) return;
            job.Status = "running";
            job.Clock.Start();
        }
        const int steps = 20;
        await using (var output = File.Create(target))
        {
            for (var i = 0; i < steps; i++)
            {
                var from = (int)((long)bytes.Length * i / steps);
                var to = (int)((long)bytes.Length * (i + 1) / steps);
                await output.WriteAsync(bytes.AsMemory(from, to - from));
                await output.FlushAsync();
                Interlocked.Exchange(ref job.BytesDone, to);
                await Task.Delay(TimeSpan.FromSeconds(seconds / steps));
            }
        }
        lock (job)
        {
            job.Clock.Stop();
            if (job.CancelRequested)
            {
                Directory.Delete(folder, true);
                return;
            }
            job.Path = target;
            job.FileName = fileName;
            job.Status = "completed";
        }
    }

    private sealed class Job
    {
        public string JobId = string.Empty;
        public DatasetOpenRequest Request = new();
        public string Status = "queued";
        public bool CancelRequested;
        public long BytesDone;
        public string? Path;
        public string? FileName;
        public readonly Stopwatch Clock = new();

        public DatasetJobResponse ToResponse()
        {
            lock (this)
            {
                return new DatasetJobResponse
                {
                    JobId = JobId,
                    Status = Status,
                    Server = Request.Server,
                    Libref = Request.Libref,
                    Member = Request.Member,
                    BytesDone = Interlocked.Read(ref BytesDone),
                    ElapsedMs = Clock.ElapsedMilliseconds,
                    Path = Path,
                    FileName = FileName,
                };
            }
        }
    }
}
