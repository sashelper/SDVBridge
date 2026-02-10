namespace MockServer.Models;

public sealed class SasServerDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsAssigned { get; set; }
}

public sealed class SasLibraryDto
{
    public string Name { get; set; } = string.Empty;
    public string Libref { get; set; } = string.Empty;
    public bool IsAssigned { get; set; }
}

public sealed class SasDatasetDto
{
    public string Member { get; set; } = string.Empty;
    public string Libref { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
}

public sealed class SasColumnDto
{
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Type { get; set; }
    public int Length { get; set; }
    public string? Format { get; set; }
    public string? Informat { get; set; }
}

public sealed class DatasetPreviewResponse
{
    public string? Server { get; set; }
    public string Libref { get; set; } = string.Empty;
    public string Member { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public int Limit { get; set; }
    public int RowCount { get; set; }
    public List<SasColumnDto> Columns { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

public sealed class DatasetOpenRequest
{
    public string? Server { get; set; }
    public string? Libref { get; set; }
    public string? Member { get; set; }
    public string? Format { get; set; }
    public int RowLimit { get; set; }
}

public sealed class DatasetExportResult
{
    public string LocalPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
}

public sealed class ProgramSubmitRequest
{
    public string? Server { get; set; }
    public string? Code { get; set; }
}

public sealed class ProgramSubmitResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public string SubmittedAt { get; set; } = string.Empty;
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class JobStatusResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string SubmittedAt { get; set; } = string.Empty;
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class JobLogResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public string Log { get; set; } = string.Empty;
    public int Offset { get; set; }
    public int NextOffset { get; set; }
    public bool IsComplete { get; set; }
}

public sealed class JobOutputResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public string Output { get; set; } = string.Empty;
}

public sealed class ProgramArtifactDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? CreatedAt { get; set; }
}

public sealed class JobArtifactsResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public List<ProgramArtifactDto> Artifacts { get; set; } = new();
}
