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
}
