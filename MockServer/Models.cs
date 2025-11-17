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
