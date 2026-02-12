using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SDVBridge.Server
{
    [DataContract]
    internal sealed class SasServerDto
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "isassigned")]
        public bool IsAssigned { get; set; }
    }

    [DataContract]
    internal sealed class SasLibraryDto
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "libref")]
        public string Libref { get; set; }

        [DataMember(Name = "isassigned")]
        public bool IsAssigned { get; set; }
    }

    [DataContract]
    internal sealed class SasDatasetDto
    {
        [DataMember(Name = "member")]
        public string Member { get; set; }

        [DataMember(Name = "libref")]
        public string Libref { get; set; }

        [DataMember(Name = "server")]
        public string Server { get; set; }
    }

    [DataContract]
    internal sealed class SasColumnDto
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "label", EmitDefaultValue = false)]
        public string Label { get; set; }

        [DataMember(Name = "type", EmitDefaultValue = false)]
        public string Type { get; set; }

        [DataMember(Name = "length", EmitDefaultValue = false)]
        public int Length { get; set; }

        [DataMember(Name = "format", EmitDefaultValue = false)]
        public string Format { get; set; }

        [DataMember(Name = "informat", EmitDefaultValue = false)]
        public string Informat { get; set; }
    }

    [DataContract]
    internal sealed class DatasetPreviewResponse
    {
        [DataMember(Name = "server", EmitDefaultValue = false)]
        public string Server { get; set; }

        [DataMember(Name = "libref")]
        public string Libref { get; set; }

        [DataMember(Name = "member")]
        public string Member { get; set; }

        [DataMember(Name = "jobid", EmitDefaultValue = false)]
        public string JobId { get; set; }

        [DataMember(Name = "limit")]
        public int Limit { get; set; }

        [DataMember(Name = "rowcount")]
        public int RowCount { get; set; }

        [DataMember(Name = "columns")]
        public List<SasColumnDto> Columns { get; set; }

        [DataMember(Name = "rows")]
        public List<Dictionary<string, string>> Rows { get; set; }
    }

    [DataContract]
    internal sealed class DatasetOpenRequest
    {
        [DataMember(Name = "server", EmitDefaultValue = false)]
        public string Server { get; set; }

        [DataMember(Name = "libref")]
        public string Libref { get; set; }

        [DataMember(Name = "member")]
        public string Member { get; set; }

        [DataMember(Name = "format", EmitDefaultValue = false)]
        public string Format { get; set; } = "sas7bdat";

        [DataMember(Name = "rowlimit", EmitDefaultValue = false)]
        public int RowLimit { get; set; }
    }

    [DataContract]
    internal sealed class DatasetExportResult
    {
        [DataMember(Name = "localpath")]
        public string LocalPath { get; set; }

        [DataMember(Name = "filename")]
        public string FileName { get; set; }

        [DataMember(Name = "contenttype")]
        public string ContentType { get; set; }
    }

    [DataContract]
    internal sealed class DatasetOpenResponse
    {
        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "filename")]
        public string FileName { get; set; }
    }

    [DataContract]
    internal sealed class ProgramSubmitRequest
    {
        [DataMember(Name = "server", EmitDefaultValue = false)]
        public string Server { get; set; }

        [DataMember(Name = "serverlogpath", EmitDefaultValue = false)]
        public string ServerLogPath { get; set; }

        [DataMember(Name = "serveroutputpath", EmitDefaultValue = false)]
        public string ServerOutputPath { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }
    }

    [DataContract]
    internal sealed class ProgramSubmitResponse
    {
        [DataMember(Name = "jobid")]
        public string JobId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "submittedat")]
        public string SubmittedAt { get; set; }

        [DataMember(Name = "startedat", EmitDefaultValue = false)]
        public string StartedAt { get; set; }

        [DataMember(Name = "completedat", EmitDefaultValue = false)]
        public string CompletedAt { get; set; }

        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string Error { get; set; }
    }

    [DataContract]
    internal sealed class JobStatusResponse
    {
        [DataMember(Name = "jobid")]
        public string JobId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "submittedat")]
        public string SubmittedAt { get; set; }

        [DataMember(Name = "startedat", EmitDefaultValue = false)]
        public string StartedAt { get; set; }

        [DataMember(Name = "completedat", EmitDefaultValue = false)]
        public string CompletedAt { get; set; }

        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string Error { get; set; }
    }

    [DataContract]
    internal sealed class JobLogResponse
    {
        [DataMember(Name = "jobid")]
        public string JobId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "log")]
        public string Log { get; set; }

        [DataMember(Name = "offset", EmitDefaultValue = false)]
        public int Offset { get; set; }

        [DataMember(Name = "nextoffset", EmitDefaultValue = false)]
        public int NextOffset { get; set; }

        [DataMember(Name = "iscomplete", EmitDefaultValue = false)]
        public bool IsComplete { get; set; }
    }

    [DataContract]
    internal sealed class JobOutputResponse
    {
        [DataMember(Name = "jobid")]
        public string JobId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "output")]
        public string Output { get; set; }
    }

    [DataContract]
    internal sealed class ProgramArtifactDto
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "contenttype", EmitDefaultValue = false)]
        public string ContentType { get; set; }

        [DataMember(Name = "sizebytes", EmitDefaultValue = false)]
        public long SizeBytes { get; set; }

        [DataMember(Name = "createdat", EmitDefaultValue = false)]
        public string CreatedAt { get; set; }
    }

    [DataContract]
    internal sealed class JobArtifactsResponse
    {
        [DataMember(Name = "jobid")]
        public string JobId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "artifacts")]
        public List<ProgramArtifactDto> Artifacts { get; set; }
    }

    [DataContract]
    internal sealed class ErrorResponse
    {
        public ErrorResponse()
        {
        }

        public ErrorResponse(string message)
        {
            Error = message;
        }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    internal sealed class ProgramJobRecord
    {
        public string JobId { get; set; }

        public string Status { get; set; }

        public string SubmittedAt { get; set; }

        public string StartedAt { get; set; }

        public string CompletedAt { get; set; }

        public string Error { get; set; }

        public string Log { get; set; }

        public string Output { get; set; }

        public List<ProgramArtifactRecord> Artifacts { get; set; }
    }

    internal sealed class ProgramArtifactRecord
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string ContentType { get; set; }

        public long SizeBytes { get; set; }

        public string CreatedAt { get; set; }
    }
}
