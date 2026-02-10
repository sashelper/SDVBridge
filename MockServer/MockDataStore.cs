using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;
using MockServer.Models;

namespace MockServer;

internal sealed class MockDataStore
{
    private const int DefaultPreviewLimit = 20;
    private const int MaxPreviewLimit = 500;

    private readonly IReadOnlyList<SasServerDto> _servers;
    private readonly Dictionary<string, IReadOnlyList<SasLibraryDto>> _libraries;
    private readonly Dictionary<string, IReadOnlyList<SasDatasetDto>> _datasets;
    private readonly Dictionary<string, IReadOnlyList<SasColumnDto>> _columns;
    private readonly ConcurrentDictionary<string, MockProgramJob> _jobs;

    public MockDataStore()
    {
        _servers = new List<SasServerDto>
        {
            new() { Name = "SASApp", IsAssigned = true },
            new() { Name = "SASAppVA", IsAssigned = false }
        };

        _libraries = new Dictionary<string, IReadOnlyList<SasLibraryDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SASApp"] = new List<SasLibraryDto>
            {
                new() { Name = "SASHELP", Libref = "SASHELP", IsAssigned = true },
                new() { Name = "WORK", Libref = "WORK", IsAssigned = true }
            },
            ["SASAppVA"] = new List<SasLibraryDto>
            {
                new() { Name = "PUBLIC", Libref = "PUBLIC", IsAssigned = true }
            }
        };

        _datasets = new Dictionary<string, IReadOnlyList<SasDatasetDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("SASApp", "SASHELP")] = new List<SasDatasetDto>
            {
                new() { Member = "CLASS", Libref = "SASHELP", Server = "SASApp" },
                new() { Member = "CARS", Libref = "SASHELP", Server = "SASApp" },
                new() { Member = "FISH", Libref = "SASHELP", Server = "SASApp" }
            },
            [Key("SASApp", "WORK")] = new List<SasDatasetDto>
            {
                new() { Member = "TEMP_USERS", Libref = "WORK", Server = "SASApp" }
            },
            [Key("SASAppVA", "PUBLIC")] = new List<SasDatasetDto>
            {
                new() { Member = "CUSTOMERS", Libref = "PUBLIC", Server = "SASAppVA" }
            }
        };

        _columns = new Dictionary<string, IReadOnlyList<SasColumnDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("SASApp", "SASHELP", "CLASS")] = new List<SasColumnDto>
            {
                new() { Name = "Name", Label = "Name", Type = "Character", Length = 8, Format = "$8.", Informat = "$8." },
                new() { Name = "Sex", Label = "Sex", Type = "Character", Length = 1, Format = "$1.", Informat = "$1." },
                new() { Name = "Age", Label = "Age", Type = "Numeric", Length = 8, Format = "BEST12.", Informat = "BEST12." },
                new() { Name = "Height", Label = "Height", Type = "Numeric", Length = 8, Format = "BEST12.", Informat = "BEST12." },
                new() { Name = "Weight", Label = "Weight", Type = "Numeric", Length = 8, Format = "BEST12.", Informat = "BEST12." }
            },
            [Key("SASApp", "SASHELP", "CARS")] = new List<SasColumnDto>
            {
                new() { Name = "Make", Label = "Make", Type = "Character", Length = 13, Format = "$13.", Informat = "$13." },
                new() { Name = "Model", Label = "Model", Type = "Character", Length = 40, Format = "$40.", Informat = "$40." },
                new() { Name = "Type", Label = "Type", Type = "Character", Length = 8, Format = "$8.", Informat = "$8." },
                new() { Name = "MSRP", Label = "MSRP", Type = "Numeric", Length = 8, Format = "DOLLAR8.", Informat = "DOLLAR8." },
                new() { Name = "Invoice", Label = "Invoice", Type = "Numeric", Length = 8, Format = "DOLLAR8.", Informat = "DOLLAR8." }
            },
            [Key("SASApp", "WORK", "TEMP_USERS")] = new List<SasColumnDto>
            {
                new() { Name = "UserId", Label = "UserId", Type = "Numeric", Length = 8, Format = "BEST12.", Informat = "BEST12." },
                new() { Name = "UserName", Label = "UserName", Type = "Character", Length = 64, Format = "$64.", Informat = "$64." },
                new() { Name = "CreatedAt", Label = "CreatedAt", Type = "Numeric", Length = 8, Format = "DATETIME20.", Informat = "DATETIME20." }
            },
            [Key("SASAppVA", "PUBLIC", "CUSTOMERS")] = new List<SasColumnDto>
            {
                new() { Name = "CustomerId", Label = "CustomerId", Type = "Numeric", Length = 8, Format = "BEST12.", Informat = "BEST12." },
                new() { Name = "Region", Label = "Region", Type = "Character", Length = 16, Format = "$16.", Informat = "$16." },
                new() { Name = "Revenue", Label = "Revenue", Type = "Numeric", Length = 8, Format = "DOLLAR12.2", Informat = "DOLLAR12.2" }
            }
        };

        _jobs = new ConcurrentDictionary<string, MockProgramJob>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SasServerDto> GetServers() => _servers;

    public bool TryGetLibraries(string server, out IReadOnlyList<SasLibraryDto> libraries)
        => _libraries.TryGetValue(server ?? string.Empty, out libraries!);

    public bool TryGetDatasets(string server, string libref, out IReadOnlyList<SasDatasetDto> datasets)
        => _datasets.TryGetValue(Key(server, libref), out datasets!);

    public bool TryGetColumns(string server, string libref, string member, out IReadOnlyList<SasColumnDto> columns)
        => _columns.TryGetValue(Key(server, libref, member), out columns!);

    public bool TryGetPreview(string server, string libref, string member, int limit, out DatasetPreviewResponse response)
    {
        response = null!;
        if (!TryGetColumns(server, libref, member, out var columns))
        {
            return false;
        }

        var normalizedLimit = NormalizePreviewLimit(limit);
        var rowList = new List<Dictionary<string, string>>();
        var columnList = columns.Select(CloneColumn).ToList();
        var keys = columnList
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        for (var rowNumber = 1; rowNumber <= normalizedLimit; rowNumber++)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columnList)
            {
                var key = string.IsNullOrWhiteSpace(column.Name) ? $"col{row.Count + 1}" : column.Name;
                row[key] = BuildPreviewValue(column, rowNumber);
            }

            if (keys.Count == 0 && row.Count == 0)
            {
                row["col1"] = rowNumber.ToString(CultureInfo.InvariantCulture);
            }

            rowList.Add(row);
        }

        response = new DatasetPreviewResponse
        {
            Server = server,
            Libref = libref,
            Member = member,
            Limit = normalizedLimit,
            RowCount = rowList.Count,
            Columns = columnList,
            Rows = rowList
        };
        return true;
    }

    public DatasetExportResult CreateDataset(DatasetOpenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Libref))
        {
            throw new InvalidOperationException("Libref is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Member))
        {
            throw new InvalidOperationException("Member is required.");
        }

        var serverName = string.IsNullOrWhiteSpace(request.Server) ? _servers[0].Name : request.Server!;
        if (!TryGetDatasets(serverName, request.Libref, out var datasets))
        {
            throw new InvalidOperationException($"Library '{request.Libref}' does not exist on server '{serverName}'.");
        }

        var dataset = datasets.FirstOrDefault(d => string.Equals(d.Member, request.Member, StringComparison.OrdinalIgnoreCase));
        if (dataset == null)
        {
            throw new InvalidOperationException($"Dataset '{request.Member}' was not found in {request.Libref}.");
        }

        var folder = CreateExportFolder(serverName, dataset.Libref);
        var fileName = $"{dataset.Member}.sas7bdat";
        var tempFile = Path.Combine(folder, fileName);
        Directory.CreateDirectory(folder);
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        var payload = BuildMockPayload(dataset, request.RowLimit > 0 ? request.RowLimit : 20);
        File.WriteAllBytes(tempFile, payload);

        return new DatasetExportResult
        {
            LocalPath = tempFile,
            FileName = fileName,
            ContentType = "application/octet-stream"
        };
    }

    public ProgramSubmitResponse SubmitProgram(ProgramSubmitRequest request)
    {
        ValidateProgramRequest(request);

        var server = string.IsNullOrWhiteSpace(request.Server) ? _servers[0].Name : request.Server!;
        var code = request.Code ?? string.Empty;
        var submittedAt = DateTimeOffset.UtcNow;
        var submittedAtText = submittedAt.ToString("o");
        var jobId = Guid.NewGuid().ToString("N");
        var log = BuildMockProgramLog(server, code);
        var output = BuildMockProgramOutput(server, code);
        var completedAt = DateTimeOffset.UtcNow;
        var completedAtText = completedAt.ToString("o");
        var artifacts = CreateMockArtifacts(jobId, server, output, submittedAtText);
        _jobs[jobId] = new MockProgramJob
        {
            JobId = jobId,
            Status = "completed",
            SubmittedAt = submittedAtText,
            StartedAt = submittedAtText,
            CompletedAt = completedAtText,
            Log = log,
            Output = output,
            Artifacts = artifacts
        };

        return new ProgramSubmitResponse
        {
            JobId = jobId,
            Status = "completed",
            SubmittedAt = submittedAtText,
            StartedAt = submittedAtText,
            CompletedAt = completedAtText
        };
    }

    public ProgramSubmitResponse QueueProgram(ProgramSubmitRequest request)
    {
        ValidateProgramRequest(request);

        var server = string.IsNullOrWhiteSpace(request.Server) ? _servers[0].Name : request.Server!;
        var code = request.Code ?? string.Empty;
        var submittedAt = DateTimeOffset.UtcNow.ToString("o");
        var jobId = Guid.NewGuid().ToString("N");
        _jobs[jobId] = new MockProgramJob
        {
            JobId = jobId,
            Status = "queued",
            SubmittedAt = submittedAt,
            Log = string.Empty,
            Output = string.Empty,
            Artifacts = new List<ProgramArtifactDto>()
        };

        _ = Task.Run(async () =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            var startedAt = DateTimeOffset.UtcNow.ToString("o");
            UpdateJob(jobId, job =>
            {
                job.Status = "running";
                job.StartedAt = startedAt;
            });

            var logLines = BuildMockProgramLogLines(server, code);
            var partialLog = new StringBuilder();
            for (var i = 0; i < logLines.Length; i++)
            {
                if (partialLog.Length > 0)
                {
                    partialLog.Append(Environment.NewLine);
                }

                partialLog.Append(logLines[i]);
                UpdateJob(jobId, job =>
                {
                    job.Log = partialLog.ToString();
                });
                await Task.Delay(120).ConfigureAwait(false);
            }

            var log = partialLog.ToString();
            var output = BuildMockProgramOutput(server, code);
            var completedAt = DateTimeOffset.UtcNow.ToString("o");
            var artifacts = CreateMockArtifacts(jobId, server, output, completedAt);
            UpdateJob(jobId, job =>
            {
                job.Status = "completed";
                job.CompletedAt = completedAt;
                job.Log = log;
                job.Output = output;
                job.Artifacts = artifacts;
            });
        });

        return new ProgramSubmitResponse
        {
            JobId = jobId,
            Status = "queued",
            SubmittedAt = submittedAt
        };
    }

    public bool TryGetJobArtifacts(string jobId, out JobArtifactsResponse response)
    {
        response = null!;
        if (!TryGetJob(jobId, out var job))
        {
            return false;
        }

        response = new JobArtifactsResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            Artifacts = job.Artifacts == null
                ? new List<ProgramArtifactDto>()
                : job.Artifacts
                    .Select(CloneArtifact)
                    .ToList()
        };
        return true;
    }

    public bool TryGetJobArtifact(string jobId, string artifactId, out ProgramArtifactDto artifact)
    {
        artifact = null!;
        if (!TryGetJob(jobId, out var job) || string.IsNullOrWhiteSpace(artifactId))
        {
            return false;
        }

        var artifacts = job.Artifacts ?? new List<ProgramArtifactDto>();
        var match = artifacts.FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase))
                    ?? artifacts.FirstOrDefault(a => string.Equals(a.Name, artifactId, StringComparison.OrdinalIgnoreCase));
        if (match == null || string.IsNullOrWhiteSpace(match.Path) || !File.Exists(match.Path))
        {
            return false;
        }

        artifact = CloneArtifact(match);
        return true;
    }

    public bool TryGetJobStatus(string jobId, out JobStatusResponse response)
    {
        response = null!;
        if (!TryGetJob(jobId, out var job))
        {
            return false;
        }

        response = new JobStatusResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            SubmittedAt = job.SubmittedAt,
            StartedAt = string.IsNullOrWhiteSpace(job.StartedAt) ? null : job.StartedAt,
            CompletedAt = string.IsNullOrWhiteSpace(job.CompletedAt) ? null : job.CompletedAt,
            Error = string.IsNullOrWhiteSpace(job.Error) ? null : job.Error
        };
        return true;
    }

    public bool TryGetJobLog(string jobId, out JobLogResponse response)
    {
        return TryGetJobLog(jobId, 0, out response);
    }

    public bool TryGetJobLog(string jobId, int offset, out JobLogResponse response)
    {
        response = null!;
        if (!TryGetJob(jobId, out var job))
        {
            return false;
        }

        var fullLog = job.Log ?? string.Empty;
        var safeOffset = offset;
        if (safeOffset < 0)
        {
            safeOffset = 0;
        }
        if (safeOffset > fullLog.Length)
        {
            safeOffset = fullLog.Length;
        }

        response = new JobLogResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            Log = safeOffset > 0 ? fullLog.Substring(safeOffset) : fullLog,
            Offset = safeOffset,
            NextOffset = fullLog.Length,
            IsComplete = string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
        };
        return true;
    }

    public bool TryGetJobOutput(string jobId, out JobOutputResponse response)
    {
        response = null!;
        if (!TryGetJob(jobId, out var job))
        {
            return false;
        }

        response = new JobOutputResponse
        {
            JobId = job.JobId,
            Status = job.Status,
            Output = job.Output
        };
        return true;
    }

    private static byte[] BuildMockPayload(SasDatasetDto dataset, int rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mock export for {dataset.Server}.{dataset.Libref}.{dataset.Member}");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"ObservationCount: {rows}");
        sb.AppendLine("\nThis is not a real SAS dataset but can be used to test streaming logic.");
        sb.AppendLine("ID,NAME,VALUE");
        for (var i = 0; i < rows; i++)
        {
            sb.AppendLine($"{i + 1},Sample {i + 1},{Random.Shared.Next(10, 9999)}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string BuildMockProgramLog(string server, string code)
    {
        return string.Join(Environment.NewLine, BuildMockProgramLogLines(server, code));
    }

    private static string[] BuildMockProgramLogLines(string server, string code)
    {
        var lineCount = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Length;
        return new[]
        {
            $"NOTE: Mock submit started on server {server}.",
            $"NOTE: Source statement count: {lineCount}.",
            "NOTE: DATA step used (Total process time):",
            "      real time           0.01 seconds",
            "NOTE: PROCEDURE PRINT used (Total process time):",
            "      real time           0.00 seconds",
            "NOTE: Mock submit completed successfully."
        };
    }

    private static string BuildMockProgramOutput(string server, string code)
    {
        var preview = code.Length <= 120 ? code : code.Substring(0, 120) + "...";
        return string.Join(Environment.NewLine, new[]
        {
            $"Mock listing output for server {server}",
            "----------------------------------------",
            "The submitted program was accepted by the mock service.",
            "Program preview:",
            preview
        });
    }

    private static List<ProgramArtifactDto> CreateMockArtifacts(string jobId, string server, string output, string createdAt)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", "Jobs", SanitizeSegment(jobId), "Artifacts");
        Directory.CreateDirectory(folder);

        var htmlPath = Path.Combine(folder, "result.html");
        var html = "<html><body><h3>Mock SAS Result</h3><pre>" + System.Net.WebUtility.HtmlEncode(output ?? string.Empty) + "</pre></body></html>";
        File.WriteAllText(htmlPath, html, Encoding.UTF8);

        var pdfPath = Path.Combine(folder, "result.pdf");
        File.WriteAllBytes(pdfPath, Encoding.UTF8.GetBytes($"%PDF-1.4\n%Mock artifact for {server}\n"));

        var xlsxPath = Path.Combine(folder, "result.xlsx");
        File.WriteAllBytes(xlsxPath, Encoding.UTF8.GetBytes("PK\u0003\u0004MOCKXLSX"));

        return new List<ProgramArtifactDto>
        {
            CreateArtifactDto(htmlPath, "text/html", createdAt),
            CreateArtifactDto(pdfPath, "application/pdf", createdAt),
            CreateArtifactDto(xlsxPath, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", createdAt)
        };
    }

    private static int NormalizePreviewLimit(int limit)
    {
        if (limit <= 0)
        {
            return DefaultPreviewLimit;
        }

        if (limit > MaxPreviewLimit)
        {
            return MaxPreviewLimit;
        }

        return limit;
    }

    private static string BuildPreviewValue(SasColumnDto column, int rowNumber)
    {
        if (column == null)
        {
            return string.Empty;
        }

        var type = column.Type ?? string.Empty;
        var format = column.Format ?? string.Empty;

        if (format.IndexOf("DATETIME", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DateTimeOffset.UtcNow
                .AddMinutes(rowNumber)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (format.IndexOf("DATE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return DateTime.UtcNow
                .Date
                .AddDays(rowNumber)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (type.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return $"{column.Name}_{rowNumber:D3}";
        }

        if (format.IndexOf("DOLLAR", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return (rowNumber * 1234.56m).ToString("0.00", CultureInfo.InvariantCulture);
        }

        return (rowNumber * 10).ToString(CultureInfo.InvariantCulture);
    }

    private static ProgramArtifactDto CreateArtifactDto(string path, string contentType, string createdAt)
    {
        var info = new FileInfo(path);
        return new ProgramArtifactDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = info.Name,
            Path = info.FullName,
            ContentType = contentType,
            SizeBytes = info.Exists ? info.Length : 0,
            CreatedAt = createdAt
        };
    }

    private static ProgramArtifactDto CloneArtifact(ProgramArtifactDto source)
    {
        return new ProgramArtifactDto
        {
            Id = source.Id,
            Name = source.Name,
            Path = source.Path,
            ContentType = source.ContentType,
            SizeBytes = source.SizeBytes,
            CreatedAt = source.CreatedAt
        };
    }

    private static SasColumnDto CloneColumn(SasColumnDto source)
    {
        return new SasColumnDto
        {
            Name = source.Name,
            Label = source.Label,
            Type = source.Type,
            Length = source.Length,
            Format = source.Format,
            Informat = source.Informat
        };
    }

    private static string CreateExportFolder(string server, string libref)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "SDVBridge", SanitizeSegment(server), SanitizeSegment(libref));
        return basePath;
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string Key(string server, string libref)
        => $"{server ?? string.Empty}::{libref ?? string.Empty}";

    private static string Key(string server, string libref, string member)
        => $"{server ?? string.Empty}::{libref ?? string.Empty}::{member ?? string.Empty}";

    private bool TryGetJob(string jobId, out MockProgramJob job)
    {
        job = null!;
        if (!_jobs.TryGetValue(jobId ?? string.Empty, out var current))
        {
            return false;
        }

        job = CloneJob(current);
        return true;
    }

    private void UpdateJob(string jobId, Action<MockProgramJob> updater)
    {
        if (string.IsNullOrWhiteSpace(jobId) || updater == null)
        {
            return;
        }

        if (!_jobs.TryGetValue(jobId, out var current))
        {
            return;
        }

        var mutable = CloneJob(current);
        updater(mutable);
        _jobs[jobId] = mutable;
    }

    private static void ValidateProgramRequest(ProgramSubmitRequest request)
    {
        if (request == null)
        {
            throw new InvalidOperationException("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new InvalidOperationException("SAS code is required.");
        }
    }

    private static MockProgramJob CloneJob(MockProgramJob source)
    {
        return new MockProgramJob
        {
            JobId = source.JobId,
            Status = source.Status,
            SubmittedAt = source.SubmittedAt,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt,
            Error = source.Error,
            Log = source.Log,
            Output = source.Output,
            Artifacts = source.Artifacts == null
                ? new List<ProgramArtifactDto>()
                : source.Artifacts.Select(CloneArtifact).ToList()
        };
    }

    private sealed class MockProgramJob
    {
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "completed";
        public string SubmittedAt { get; set; } = string.Empty;
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
        public string? Error { get; set; }
        public string Log { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public List<ProgramArtifactDto> Artifacts { get; set; } = new();
    }
}
