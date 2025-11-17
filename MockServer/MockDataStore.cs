using System.Linq;
using System.Text;
using MockServer.Models;

namespace MockServer;

internal sealed class MockDataStore
{
    private readonly IReadOnlyList<SasServerDto> _servers;
    private readonly Dictionary<string, IReadOnlyList<SasLibraryDto>> _libraries;
    private readonly Dictionary<string, IReadOnlyList<SasDatasetDto>> _datasets;

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
    }

    public IReadOnlyList<SasServerDto> GetServers() => _servers;

    public bool TryGetLibraries(string server, out IReadOnlyList<SasLibraryDto> libraries)
        => _libraries.TryGetValue(server ?? string.Empty, out libraries!);

    public bool TryGetDatasets(string server, string libref, out IReadOnlyList<SasDatasetDto> datasets)
        => _datasets.TryGetValue(Key(server, libref), out datasets!);

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
}
