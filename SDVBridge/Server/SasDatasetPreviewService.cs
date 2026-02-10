using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

namespace SDVBridge.Server
{
    internal sealed class SasDatasetPreviewService
    {
        private const string BeginMarker = "__SDV_PREVIEW_BEGIN__";
        private const string EndMarker = "__SDV_PREVIEW_END__";
        private const string RowMarker = "__SDV_PREVIEW_ROW__|";
        private const int DefaultLimit = 20;
        private const int MaxLimit = 500;

        private readonly SasMetadataService _metadataService;
        private readonly SasProgramService _programService;

        public SasDatasetPreviewService(SasMetadataService metadataService, SasProgramService programService)
        {
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
            _programService = programService ?? throw new ArgumentNullException(nameof(programService));
        }

        public async Task<DatasetPreviewResponse> GetPreviewAsync(string server, string libref, string member, int limit)
        {
            if (string.IsNullOrWhiteSpace(libref))
            {
                throw new ArgumentException("Libref is required.", nameof(libref));
            }

            if (string.IsNullOrWhiteSpace(member))
            {
                throw new ArgumentException("Dataset member is required.", nameof(member));
            }

            var normalizedLimit = NormalizeLimit(limit);
            var columns = (await _metadataService.GetColumnsAsync(server, libref, member).ConfigureAwait(false))
                ?.ToList() ?? new List<SasColumnDto>();

            var submitResponse = await _programService.SubmitAsync(new ProgramSubmitRequest
            {
                Server = server,
                Code = BuildPreviewCode(libref, member, normalizedLimit)
            }).ConfigureAwait(false);

            if (!_programService.TryGetLog(submitResponse.JobId, out var logResponse))
            {
                throw new InvalidOperationException($"Preview job '{submitResponse.JobId}' log was not found.");
            }

            if (!string.Equals(submitResponse.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(submitResponse.Error)
                        ? "SAS preview program failed."
                        : submitResponse.Error);
            }

            var logText = logResponse.Log ?? string.Empty;
            if (!ContainsMarker(logText, BeginMarker) || !ContainsMarker(logText, EndMarker))
            {
                throw new InvalidOperationException("Unable to parse dataset preview from SAS log.");
            }

            var csvLines = ExtractCsvLines(logText);
            if (csvLines.Count == 0)
            {
                throw new InvalidOperationException("No preview data was captured from SAS log.");
            }

            var rows = ParseRows(csvLines, columns, normalizedLimit);

            return new DatasetPreviewResponse
            {
                Server = server,
                Libref = libref,
                Member = member,
                JobId = submitResponse.JobId,
                Limit = normalizedLimit,
                RowCount = rows.Count,
                Columns = columns,
                Rows = rows
            };
        }

        private static int NormalizeLimit(int requestedLimit)
        {
            if (requestedLimit <= 0)
            {
                return DefaultLimit;
            }

            if (requestedLimit > MaxLimit)
            {
                return MaxLimit;
            }

            return requestedLimit;
        }

        private static string BuildPreviewCode(string libref, string member, int limit)
        {
            var datasetRef = $"{ToNameLiteral(libref)}.{ToNameLiteral(member)}";
            var sb = new StringBuilder();
            sb.AppendLine("filename _sdvprvw temp;");
            sb.AppendLine($"proc export data={datasetRef}(obs={limit}) outfile=_sdvprvw dbms=csv replace;");
            sb.AppendLine("run;");
            sb.AppendLine("data _null_;");
            sb.AppendLine($"  putlog '{BeginMarker}';");
            sb.AppendLine("run;");
            sb.AppendLine("data _null_;");
            sb.AppendLine("  infile _sdvprvw lrecl=32767 truncover;");
            sb.AppendLine("  input;");
            sb.AppendLine($"  putlog '{RowMarker}' _infile_;");
            sb.AppendLine("run;");
            sb.AppendLine("data _null_;");
            sb.AppendLine($"  putlog '{EndMarker}';");
            sb.AppendLine("run;");
            sb.AppendLine("filename _sdvprvw clear;");
            return sb.ToString();
        }

        private static string ToNameLiteral(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("SAS name literal value cannot be empty.", nameof(value));
            }

            return $"'{value.Replace("'", "''")}'n";
        }

        private static bool ContainsMarker(string text, string marker)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
            {
                return false;
            }

            return text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> ExtractCsvLines(string logText)
        {
            var rows = new List<string>();
            if (string.IsNullOrWhiteSpace(logText))
            {
                return rows;
            }

            var lines = logText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var inside = false;
            foreach (var line in lines)
            {
                if (line.IndexOf(BeginMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inside = true;
                    continue;
                }

                if (line.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    break;
                }

                if (!inside)
                {
                    continue;
                }

                var markerIndex = line.IndexOf(RowMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                rows.Add(line.Substring(markerIndex + RowMarker.Length).TrimStart());
            }

            return rows;
        }

        private static List<Dictionary<string, string>> ParseRows(IList<string> csvLines, IList<SasColumnDto> columns, int limit)
        {
            var rows = new List<Dictionary<string, string>>();
            if (csvLines == null || csvLines.Count == 0 || limit <= 0)
            {
                return rows;
            }

            var header = ParseCsvFields(csvLines[0]);
            var keys = BuildKeys(columns, header);

            for (var i = 1; i < csvLines.Count && rows.Count < limit; i++)
            {
                var fields = ParseCsvFields(csvLines[i]);
                if (fields.Length == 0)
                {
                    continue;
                }

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var maxCount = Math.Max(keys.Count, fields.Length);
                for (var j = 0; j < maxCount; j++)
                {
                    var key = j < keys.Count ? keys[j] : $"col{j + 1}";
                    key = string.IsNullOrWhiteSpace(key) ? $"col{j + 1}" : key;
                    key = EnsureUniqueKey(row, key);
                    row[key] = j < fields.Length ? fields[j] : string.Empty;
                }

                rows.Add(row);
            }

            return rows;
        }

        private static List<string> BuildKeys(IList<SasColumnDto> columns, string[] headerFields)
        {
            var keys = new List<string>();

            if (columns != null)
            {
                keys.AddRange(columns
                    .Select(c => c?.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)));
            }

            if (keys.Count == 0 && headerFields != null && headerFields.Length > 0)
            {
                keys.AddRange(headerFields.Select((name, idx) =>
                    string.IsNullOrWhiteSpace(name) ? $"col{idx + 1}" : name));
            }

            if (keys.Count == 0)
            {
                keys.Add("col1");
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = $"col{i + 1}";
                }

                if (!used.Add(key))
                {
                    var suffix = 2;
                    while (!used.Add($"{key}_{suffix}"))
                    {
                        suffix++;
                    }

                    key = $"{key}_{suffix}";
                }

                keys[i] = key;
            }

            return keys;
        }

        private static string EnsureUniqueKey(Dictionary<string, string> row, string baseKey)
        {
            if (!row.ContainsKey(baseKey))
            {
                return baseKey;
            }

            var suffix = 2;
            var key = $"{baseKey}_{suffix}";
            while (row.ContainsKey(key))
            {
                suffix++;
                key = $"{baseKey}_{suffix}";
            }

            return key;
        }

        private static string[] ParseCsvFields(string line)
        {
            if (line == null)
            {
                return new string[0];
            }

            try
            {
                using (var reader = new StringReader(line))
                using (var parser = new TextFieldParser(reader))
                {
                    parser.SetDelimiters(",");
                    parser.HasFieldsEnclosedInQuotes = true;
                    parser.TrimWhiteSpace = false;
                    return parser.ReadFields() ?? new string[0];
                }
            }
            catch
            {
                return ParseCsvFieldsFallback(line);
            }
        }

        private static string[] ParseCsvFieldsFallback(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
