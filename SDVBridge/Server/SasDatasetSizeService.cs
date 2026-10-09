using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace SDVBridge.Server
{
    internal sealed class SasDatasetSizeService
    {
        private const string SizeMarker = "__SDV_SIZE__|";

        private readonly SasProgramService _programService;

        public SasDatasetSizeService(SasProgramService programService)
        {
            _programService = programService ?? throw new ArgumentNullException(nameof(programService));
        }

        public async Task<DatasetSizeResponse> GetSizeAsync(string server, string libref, string member)
        {
            if (string.IsNullOrWhiteSpace(libref))
            {
                throw new ArgumentException("Libref is required.", nameof(libref));
            }

            if (string.IsNullOrWhiteSpace(member))
            {
                throw new ArgumentException("Dataset member is required.", nameof(member));
            }

            var submitResponse = await _programService.SubmitAsync(new ProgramSubmitRequest
            {
                Server = server,
                Code = BuildSizeCode(libref, member)
            }).ConfigureAwait(false);

            if (!_programService.TryGetLog(submitResponse.JobId, out var logResponse))
            {
                throw new InvalidOperationException($"Dataset size job '{submitResponse.JobId}' log was not found.");
            }

            if (!string.Equals(submitResponse.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(submitResponse.Error)
                        ? "SAS dataset size program failed."
                        : submitResponse.Error);
            }

            var values = ExtractMarkerValues(logResponse.Log ?? string.Empty);
            if (values.Count == 0)
            {
                throw new InvalidOperationException("Unable to parse dataset size from SAS log.");
            }

            var available = ParseBool(GetValue(values, "available"));
            var isEstimate = ParseBool(GetValue(values, "isestimate"));
            var sizeBytes = ParseNonNegativeLong(GetValue(values, "sizebytes"));
            var nobs = ParseNonNegativeLong(GetValue(values, "nobs"));
            var obsLen = ParseNonNegativeLong(GetValue(values, "obslen"));
            var nvar = ParseNonNegativeInt(GetValue(values, "nvar"));

            if (!available)
            {
                sizeBytes = 0;
            }

            return new DatasetSizeResponse
            {
                Server = server,
                Libref = libref,
                Member = member,
                JobId = submitResponse.JobId,
                Available = available,
                SizeBytes = sizeBytes,
                IsEstimate = isEstimate,
                Nobs = nobs,
                ObsLen = obsLen,
                Nvar = nvar,
                Source = GetValue(values, "source")
            };
        }

        private static string BuildSizeCode(string libref, string member)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%let _sdv_size_sqlobs=0;");
            sb.AppendLine("%let _sdv_size_filesize=;");
            sb.AppendLine("%let _sdv_size_nobs=;");
            sb.AppendLine("%let _sdv_size_obslen=;");
            sb.AppendLine("%let _sdv_size_nvar=;");
            sb.AppendLine("proc sql noprint outobs=1;");
            sb.AppendLine("  select filesize, nobs, obslen, nvar");
            sb.AppendLine("    into :_sdv_size_filesize trimmed,");
            sb.AppendLine("         :_sdv_size_nobs trimmed,");
            sb.AppendLine("         :_sdv_size_obslen trimmed,");
            sb.AppendLine("         :_sdv_size_nvar trimmed");
            sb.AppendLine("  from dictionary.tables");
            sb.AppendLine($"  where upcase(libname)=upcase({ToSasStringLiteral(libref)})");
            sb.AppendLine($"    and upcase(memname)=upcase({ToSasStringLiteral(member)})");
            sb.AppendLine("    and memtype='DATA';");
            sb.AppendLine("quit;");
            sb.AppendLine("%let _sdv_size_sqlobs=&sqlobs;");
            sb.AppendLine("data _null_;");
            sb.AppendLine("  length sizeText nobsText obslenText nvarText $32 available estimate $5 source $32;");
            sb.AppendLine("  filesize = input(symget('_sdv_size_filesize'), ?? best32.);");
            sb.AppendLine("  nobs = input(symget('_sdv_size_nobs'), ?? best32.);");
            sb.AppendLine("  obslen = input(symget('_sdv_size_obslen'), ?? best32.);");
            sb.AppendLine("  nvar = input(symget('_sdv_size_nvar'), ?? best32.);");
            sb.AppendLine("  sqlobs = input(symget('_sdv_size_sqlobs'), ?? best32.);");
            sb.AppendLine("  call missing(sizebytes);");
            sb.AppendLine("  available = 'false';");
            sb.AppendLine("  estimate = 'false';");
            sb.AppendLine("  source = 'unavailable';");
            sb.AppendLine("  if sqlobs > 0 then do;");
            sb.AppendLine("    if not missing(filesize) and filesize >= 0 then do;");
            sb.AppendLine("      sizebytes = filesize;");
            sb.AppendLine("      available = 'true';");
            sb.AppendLine("      source = 'dictionary.tables.filesize';");
            sb.AppendLine("    end;");
            sb.AppendLine("    else if not missing(nobs) and nobs >= 0 and not missing(obslen) and obslen >= 0 then do;");
            sb.AppendLine("      sizebytes = nobs * obslen;");
            sb.AppendLine("      available = 'true';");
            sb.AppendLine("      estimate = 'true';");
            sb.AppendLine("      source = 'dictionary.tables.nobs_obslen';");
            sb.AppendLine("    end;");
            sb.AppendLine("  end;");
            sb.AppendLine("  sizeText = ifc(missing(sizebytes), '', strip(put(sizebytes, 32.)));");
            sb.AppendLine("  nobsText = ifc(missing(nobs), '', strip(put(nobs, 32.)));");
            sb.AppendLine("  obslenText = ifc(missing(obslen), '', strip(put(obslen, 32.)));");
            sb.AppendLine("  nvarText = ifc(missing(nvar), '', strip(put(nvar, 32.)));");
            sb.AppendLine($"  putlog cats('{SizeMarker}',");
            sb.AppendLine("    'available=', available,");
            sb.AppendLine("    '|sizebytes=', sizeText,");
            sb.AppendLine("    '|isestimate=', estimate,");
            sb.AppendLine("    '|nobs=', nobsText,");
            sb.AppendLine("    '|obslen=', obslenText,");
            sb.AppendLine("    '|nvar=', nvarText,");
            sb.AppendLine("    '|source=', source);");
            sb.AppendLine("run;");
            sb.AppendLine("%symdel _sdv_size_sqlobs _sdv_size_filesize _sdv_size_nobs _sdv_size_obslen _sdv_size_nvar / nowarn;");
            return sb.ToString();
        }

        private static string ToSasStringLiteral(string value)
        {
            return $"'{(value ?? string.Empty).Replace("'", "''")}'";
        }

        private static Dictionary<string, string> ExtractMarkerValues(string logText)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(logText))
            {
                return values;
            }

            var lines = logText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var markerIndex = line.IndexOf(SizeMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var payload = line.Substring(markerIndex + SizeMarker.Length).Trim();
                foreach (var part in payload.Split('|'))
                {
                    if (string.IsNullOrWhiteSpace(part))
                    {
                        continue;
                    }

                    var separator = part.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    values[part.Substring(0, separator).Trim()] = part.Substring(separator + 1).Trim();
                }

                break;
            }

            return values;
        }

        private static string GetValue(IDictionary<string, string> values, string key)
        {
            if (values == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return values.TryGetValue(key, out var value) ? value : null;
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static long ParseNonNegativeLong(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed < 0 ? 0 : parsed;
            }

            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
            {
                if (decimalValue <= 0)
                {
                    return 0;
                }

                return decimalValue >= long.MaxValue ? long.MaxValue : decimal.ToInt64(decimal.Truncate(decimalValue));
            }

            return 0;
        }

        private static int ParseNonNegativeInt(string value)
        {
            var parsed = ParseNonNegativeLong(value);
            return parsed > int.MaxValue ? int.MaxValue : (int)parsed;
        }
    }
}
