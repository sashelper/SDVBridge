using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SAS.Tasks.Toolkit;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal sealed class SasProgramService
    {
        private static readonly string[] SubmitMethodNames =
        {
            "SubmitCode",
            "SubmitSASProgramAndWait",
            "SubmitSasProgramAndWait",
            "SubmitSASProgram",
            "SubmitSasProgram",
            "SubmitSynchronous"
        };

        private static readonly string[] LogMemberNames =
        {
            "RunLog",
            "RunLogText",
            "ExecutionLog",
            "SubmitLog",
            "SubmissionLog",
            "SasRunLog",
            "SASRunLog",
            "SasLogText",
            "SASLogText",
            "LogText",
            "TextLog",
            "Log",
            "SasLog",
            "SASLog",
            "XmlLog",
            "LogXml",
            "Text",
            "Contents",
            "Messages",
            "MessageText",
            "Diagnostics"
        };

        private static readonly string[] OutputMemberNames =
        {
            "Output",
            "Listing",
            "ListOutput",
            "RunOutput",
            "ExecutionOutput",
            "OutputText",
            "ListingText",
            "Results",
            "Result",
            "XmlResult",
            "HtmlResult",
            "TextResult",
            "Text",
            "Contents"
        };

        private static readonly string[] CompletionMemberNames =
        {
            "IsComplete",
            "IsCompleted",
            "Complete",
            "Completed",
            "Done",
            "IsDone"
        };

        private static readonly string[] BusyMemberNames =
        {
            "IsBusy",
            "Busy",
            "IsRunning",
            "Running",
            "SubmitPending",
            "IsSubmitting",
            "Submitting",
            "IsExecuting",
            "Executing",
            "InProgress"
        };

        private static readonly string[] StatusMemberNames =
        {
            "Status",
            "State",
            "RunStatus",
            "SubmitStatus",
            "ExecutionStatus"
        };

        private const string TempLogFileref = "_sdvlog";
        private const string TempOutputFileref = "_sdvlst";

        private readonly EgInteropContext _context;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, ProgramJobRecord> _jobs = new Dictionary<string, ProgramJobRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _jobOrder = new Queue<string>();
        private readonly SemaphoreSlim _submitGate = new SemaphoreSlim(1, 1);

        public SasProgramService(EgInteropContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<ProgramSubmitResponse> SubmitAsync(ProgramSubmitRequest request)
        {
            ValidateRequest(request);

            var now = UtcNow();
            var job = new ProgramJobRecord
            {
                JobId = Guid.NewGuid().ToString("N"),
                Status = "running",
                SubmittedAt = now,
                StartedAt = now,
                Log = string.Empty,
                Output = string.Empty,
                Artifacts = new List<ProgramArtifactRecord>()
            };

            StoreJob(job);
            await ExecuteJobCoreAsync(job.JobId, CloneRequest(request), true).ConfigureAwait(false);
            var finalState = FindJob(job.JobId) ?? job;
            return ToSubmitResponse(finalState);
        }

        public ProgramSubmitResponse QueueSubmit(ProgramSubmitRequest request)
        {
            ValidateRequest(request);

            var job = new ProgramJobRecord
            {
                JobId = Guid.NewGuid().ToString("N"),
                Status = "queued",
                SubmittedAt = UtcNow(),
                Log = string.Empty,
                Output = string.Empty,
                Artifacts = new List<ProgramArtifactRecord>()
            };

            StoreJob(job);
            var requestCopy = CloneRequest(request);
            var jobId = job.JobId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteJobCoreAsync(jobId, requestCopy, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var completedAt = UtcNow();
                    UpdateJob(jobId, record =>
                    {
                        record.Status = "failed";
                        record.Error = ex.Message;
                        record.Log = FirstNonEmpty(record.Log, ex.ToString()) ?? ex.Message;
                        record.CompletedAt = completedAt;
                        if (string.IsNullOrWhiteSpace(record.StartedAt))
                        {
                            record.StartedAt = completedAt;
                        }
                    });
                    Log($"Program submit job {jobId} failed in queue worker: {ex.Message}");
                }
            });

            return ToSubmitResponse(job);
        }

        public bool TryGetStatus(string jobId, out JobStatusResponse response)
        {
            response = null;
            var record = FindJob(jobId);
            if (record == null)
            {
                return false;
            }

            response = new JobStatusResponse
            {
                JobId = record.JobId,
                Status = record.Status,
                SubmittedAt = record.SubmittedAt,
                StartedAt = record.StartedAt,
                CompletedAt = record.CompletedAt,
                Error = record.Error
            };
            return true;
        }

        public bool TryGetLog(string jobId, out JobLogResponse response)
        {
            return TryGetLog(jobId, 0, out response);
        }

        public bool TryGetLog(string jobId, int offset, out JobLogResponse response)
        {
            response = null;
            var record = FindJob(jobId);
            if (record == null)
            {
                return false;
            }

            var fullLog = record.Log ?? string.Empty;
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
                JobId = record.JobId,
                Status = record.Status,
                Log = safeOffset > 0 ? fullLog.Substring(safeOffset) : fullLog,
                Offset = safeOffset,
                NextOffset = fullLog.Length,
                IsComplete = string.Equals(record.Status, "completed", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(record.Status, "failed", StringComparison.OrdinalIgnoreCase)
            };
            return true;
        }

        public bool TryGetArtifacts(string jobId, out JobArtifactsResponse response)
        {
            response = null;
            var record = FindJob(jobId);
            if (record == null)
            {
                return false;
            }

            var artifacts = record.Artifacts ?? new List<ProgramArtifactRecord>();
            response = new JobArtifactsResponse
            {
                JobId = record.JobId,
                Status = record.Status,
                Artifacts = artifacts
                    .Select(a => new ProgramArtifactDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Path = a.Path,
                        ContentType = a.ContentType,
                        SizeBytes = a.SizeBytes,
                        CreatedAt = a.CreatedAt
                    })
                    .ToList()
            };
            return true;
        }

        public bool TryGetArtifact(string jobId, string artifactId, out ProgramArtifactRecord artifact)
        {
            artifact = null;
            var record = FindJob(jobId);
            if (record == null || string.IsNullOrWhiteSpace(artifactId))
            {
                return false;
            }

            var artifacts = record.Artifacts ?? new List<ProgramArtifactRecord>();
            var match = artifacts.FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase))
                        ?? artifacts.FirstOrDefault(a => string.Equals(a.Name, artifactId, StringComparison.OrdinalIgnoreCase));
            if (match == null || string.IsNullOrWhiteSpace(match.Path) || !File.Exists(match.Path))
            {
                return false;
            }

            artifact = CloneArtifactRecord(match);
            return true;
        }

        public bool TryGetOutput(string jobId, out JobOutputResponse response)
        {
            response = null;
            var record = FindJob(jobId);
            if (record == null)
            {
                return false;
            }

            response = new JobOutputResponse
            {
                JobId = record.JobId,
                Status = record.Status,
                Output = record.Output ?? string.Empty
            };
            return true;
        }

        private async Task ExecuteJobCoreAsync(string jobId, ProgramSubmitRequest request, bool alreadyRunning)
        {
            await _submitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!alreadyRunning)
                {
                    var startedAt = UtcNow();
                    UpdateJob(jobId, record =>
                    {
                        record.Status = "running";
                        record.StartedAt = startedAt;
                    });
                }

                ProgramExecutionResult execution;
                try
                {
                    execution = await _context.RunOnUiAsync(() => ExecuteSubmitCore(request, jobId)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    execution = ProgramExecutionResult.Failed(
                        ex.Message,
                        ex.ToString(),
                        string.Empty,
                        BuildArtifacts(jobId, null, null, null, ex.ToString(), string.Empty));
                }

                var completedAt = UtcNow();
                UpdateJob(jobId, record =>
                {
                    record.Status = execution.Status;
                    record.Error = execution.Error;
                    record.Log = execution.Log ?? string.Empty;
                    record.Output = execution.Output ?? string.Empty;
                    record.Artifacts = execution.Artifacts ?? new List<ProgramArtifactRecord>();
                    record.CompletedAt = completedAt;
                    if (string.IsNullOrWhiteSpace(record.StartedAt))
                    {
                        record.StartedAt = completedAt;
                    }
                });

                Log($"Program submit job {jobId} completed with status '{execution.Status}'.");
            }
            finally
            {
                _submitGate.Release();
            }
        }

        private ProgramExecutionResult ExecuteSubmitCore(ProgramSubmitRequest request, string jobId)
        {
            object submitter = null;
            object submitResult = null;
            SasServer captureServer = null;
            ProgramCaptureFiles captureFiles = null;
            try
            {
                var code = request.Code ?? string.Empty;
                submitter = GetMemberValue(_context.Consumer, "Submit");
                var configuredServerLogPath = FirstNonEmpty(
                    NormalizeOptionalPath(request.ServerLogPath),
                    NormalizeOptionalPath(WebServerManager.DefaultServerLogPath));
                var configuredServerOutputPath = FirstNonEmpty(
                    NormalizeOptionalPath(request.ServerOutputPath),
                    NormalizeOptionalPath(WebServerManager.DefaultServerOutputPath));
                var preferredServerName = string.IsNullOrWhiteSpace(request.Server)
                    ? (_context.Consumer == null ? null : _context.Consumer.AssignedServer)
                    : request.Server;
                captureServer = ResolveSasServer(preferredServerName);
                if (captureServer == null)
                {
                    Log($"Capture server was not resolved for preferred server '{preferredServerName ?? "<null>"}'; falling back to local capture paths.");
                }
                else
                {
                    Log($"Capture server resolved: Name='{captureServer.Name}', DisplayName='{captureServer.DisplayName}'.");
                }

                captureFiles = CreateProgramCaptureFiles(
                    jobId,
                    captureServer,
                    configuredServerLogPath,
                    configuredServerOutputPath);
                var submitCode = WrapSubmittedCode(code, captureFiles);
                Log($"Using PROC PRINTTO capture files for job {jobId}: submitLog='{captureFiles.SubmitLogPath}', submitOutput='{captureFiles.SubmitOutputPath}', localLog='{captureFiles.LogPath}', localOutput='{captureFiles.OutputPath}', mode={(captureFiles.UsesServerCapture ? "server" : "local")}, tempFilerefMode={captureFiles.UseTempServerFileref}, configuredServerPaths={captureFiles.UsesConfiguredServerPaths}.");
                Log($"Submitting SAS code for job {jobId}:{Environment.NewLine}{submitCode}");

                PublishProgress(jobId, submitResult, submitter, _context.Consumer);
                PublishProgressFromFiles(jobId, captureFiles.LogPath, captureFiles.OutputPath);
                submitResult = InvokeSubmit(
                    submitCode,
                    request.Server,
                    submitter,
                    _context.Consumer,
                    (result, submitTarget, consumerTarget) =>
                    {
                        PublishProgress(jobId, result, submitTarget, consumerTarget);
                        PublishProgressFromFiles(jobId, captureFiles.LogPath, captureFiles.OutputPath);
                    });

                if (captureFiles.UsesServerCapture)
                {
                    DownloadServerCaptureFiles(captureFiles, captureServer);
                }
                PublishProgressFromFiles(jobId, captureFiles.LogPath, captureFiles.OutputPath);
                var log = FirstNonEmpty(
                    TryReadFileText(captureFiles.LogPath),
                    TryReadText(submitResult, LogMemberNames),
                    TryReadText(submitter, LogMemberNames),
                    TryReadText(_context.Consumer, LogMemberNames)) ?? string.Empty;

                var output = FirstNonEmpty(
                    TryReadFileText(captureFiles.OutputPath),
                    TryReadText(submitResult, OutputMemberNames),
                    TryReadText(submitter, OutputMemberNames),
                    TryReadText(_context.Consumer, OutputMemberNames)) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(log))
                {
                    LogTextExtractionProbe(jobId, "completed-empty-log", submitResult, submitter, _context.Consumer);
                }

                PublishProgress(jobId, submitResult, submitter, _context.Consumer);
                var artifacts = BuildArtifacts(jobId, submitResult, submitter, _context.Consumer, log, output);
                return ProgramExecutionResult.Completed(log, output, artifacts);
            }
            catch (Exception ex)
            {
                if (captureFiles != null && captureFiles.UsesServerCapture)
                {
                    DownloadServerCaptureFiles(captureFiles, captureServer);
                }
                PublishProgressFromFiles(
                    jobId,
                    captureFiles == null ? null : captureFiles.LogPath,
                    captureFiles == null ? null : captureFiles.OutputPath);
                var failedLog = FirstNonEmpty(
                    captureFiles == null ? null : TryReadFileText(captureFiles.LogPath),
                    TryReadText(submitResult, LogMemberNames),
                    TryReadText(submitter, LogMemberNames),
                    TryReadText(_context.Consumer, LogMemberNames),
                    ex.ToString()) ?? ex.Message;

                var failedOutput = FirstNonEmpty(
                    captureFiles == null ? null : TryReadFileText(captureFiles.OutputPath),
                    TryReadText(submitResult, OutputMemberNames),
                    TryReadText(submitter, OutputMemberNames),
                    TryReadText(_context.Consumer, OutputMemberNames)) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(failedLog))
                {
                    LogTextExtractionProbe(jobId, "failed-empty-log", submitResult, submitter, _context.Consumer);
                }

                PublishProgress(jobId, submitResult, submitter, _context.Consumer);
                var failedArtifacts = BuildArtifacts(jobId, submitResult, submitter, _context.Consumer, failedLog, failedOutput);
                return ProgramExecutionResult.Failed(ex.Message, failedLog, failedOutput, failedArtifacts);
            }
        }

        private void PublishProgress(string jobId, object submitResult, object submitter, object consumer)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return;
            }

            var currentLog = FirstNonEmpty(
                TryReadText(submitResult, LogMemberNames),
                TryReadText(submitter, LogMemberNames),
                TryReadText(consumer, LogMemberNames));

            var currentOutput = FirstNonEmpty(
                TryReadText(submitResult, OutputMemberNames),
                TryReadText(submitter, OutputMemberNames),
                TryReadText(consumer, OutputMemberNames));

            UpdateJob(jobId, record =>
            {
                if (!string.IsNullOrWhiteSpace(currentLog) &&
                    !string.Equals(record.Log, currentLog, StringComparison.Ordinal))
                {
                    record.Log = currentLog;
                }

                if (!string.IsNullOrWhiteSpace(currentOutput) &&
                    !string.Equals(record.Output, currentOutput, StringComparison.Ordinal))
                {
                    record.Output = currentOutput;
                }
            });
        }

        private void PublishProgressFromFiles(string jobId, string logPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return;
            }

            var currentLog = TryReadFileText(logPath);
            var currentOutput = TryReadFileText(outputPath);
            if (string.IsNullOrWhiteSpace(currentLog) && string.IsNullOrWhiteSpace(currentOutput))
            {
                return;
            }

            UpdateJob(jobId, record =>
            {
                if (!string.IsNullOrWhiteSpace(currentLog) &&
                    !string.Equals(record.Log, currentLog, StringComparison.Ordinal))
                {
                    record.Log = currentLog;
                }

                if (!string.IsNullOrWhiteSpace(currentOutput) &&
                    !string.Equals(record.Output, currentOutput, StringComparison.Ordinal))
                {
                    record.Output = currentOutput;
                }
            });
        }

        private static void LogTextExtractionProbe(string jobId, string phase, object submitResult, object submitter, object consumer)
        {
            try
            {
                var details = new[]
                {
                    BuildProbeDetails("submitResult", submitResult),
                    BuildProbeDetails("submitter", submitter),
                    BuildProbeDetails("consumer", consumer)
                };

                Log($"Log probe job={jobId}, phase={phase}{Environment.NewLine}{string.Join(Environment.NewLine, details)}");
            }
            catch (Exception ex)
            {
                Log($"Log probe failed for job={jobId}: {ex.Message}");
            }
        }

        private static string BuildProbeDetails(string label, object target)
        {
            if (target == null)
            {
                return $"{label}: <null>";
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            var lines = new List<string>
            {
                $"{label}: {type.FullName}"
            };

            var interesting = type
                .GetMembers(flags)
                .Where(m =>
                {
                    var name = m.Name ?? string.Empty;
                    return name.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("result", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("list", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .OrderBy(m => m.Name)
                .Take(40)
                .ToList();

            if (interesting.Count == 0)
            {
                lines.Add("  (no interesting members)");
                return string.Join(Environment.NewLine, lines);
            }

            foreach (var member in interesting)
            {
                object value = null;
                var hasValue = false;
                try
                {
                    if (member is PropertyInfo prop && prop.GetIndexParameters().Length == 0)
                    {
                        value = prop.GetValue(target);
                        hasValue = true;
                    }
                    else if (member is FieldInfo field)
                    {
                        value = field.GetValue(target);
                        hasValue = true;
                    }
                }
                catch
                {
                    // ignore member access failures in diagnostic probe
                }

                if (!hasValue)
                {
                    lines.Add($"  {member.MemberType} {member.Name}: <unreadable>");
                    continue;
                }

                var preview = SummarizeProbeValue(value);
                lines.Add($"  {member.MemberType} {member.Name}: {preview}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string SummarizeProbeValue(object value)
        {
            if (value == null)
            {
                return "<null>";
            }

            if (value is string text)
            {
                var compact = text.Replace("\r", "\\r").Replace("\n", "\\n");
                if (compact.Length > 160)
                {
                    compact = compact.Substring(0, 160) + "...";
                }

                return $"string(len={text.Length}) \"{compact}\"";
            }

            if (value is IEnumerable sequence && !(value is string))
            {
                var count = 0;
                object first = null;
                foreach (var item in sequence)
                {
                    if (count == 0)
                    {
                        first = item;
                    }

                    count++;
                    if (count >= 50)
                    {
                        break;
                    }
                }

                var firstType = first == null ? "null" : first.GetType().FullName;
                return $"{value.GetType().FullName} (enumerable, sampledCount={count}, first={firstType})";
            }

            return $"{value.GetType().FullName} value={value}";
        }

        private static ProgramSubmitResponse ToSubmitResponse(ProgramJobRecord job)
        {
            return new ProgramSubmitResponse
            {
                JobId = job.JobId,
                Status = job.Status,
                SubmittedAt = job.SubmittedAt,
                StartedAt = job.StartedAt,
                CompletedAt = job.CompletedAt,
                Error = job.Error
            };
        }

        private static ProgramSubmitRequest CloneRequest(ProgramSubmitRequest request)
        {
            return new ProgramSubmitRequest
            {
                Server = request.Server,
                ServerLogPath = request.ServerLogPath,
                ServerOutputPath = request.ServerOutputPath,
                Code = request.Code
            };
        }

        private static void ValidateRequest(ProgramSubmitRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                throw new ArgumentException("SAS code is required.", nameof(request.Code));
            }

            var hasServerLogPath = !string.IsNullOrWhiteSpace(request.ServerLogPath);
            var hasServerOutputPath = !string.IsNullOrWhiteSpace(request.ServerOutputPath);
            if (hasServerLogPath != hasServerOutputPath)
            {
                throw new ArgumentException("ServerLogPath and ServerOutputPath must be provided together.");
            }
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString("o");
        }

        private List<ProgramArtifactRecord> BuildArtifacts(string jobId, object submitResult, object submitter, object consumer, string log, string output)
        {
            var artifacts = new List<ProgramArtifactRecord>();
            var folder = CreateArtifactFolder(jobId);
            var createdAt = UtcNow();
            var copiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in FindArtifactFilePaths(submitResult, submitter, consumer))
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!File.Exists(fullPath) || !copiedPaths.Add(fullPath))
                    {
                        continue;
                    }

                    var destination = EnsureUniqueFilePath(folder, Path.GetFileName(fullPath));
                    File.Copy(fullPath, destination, true);
                    var copied = CreateArtifactRecord(destination, createdAt, null);
                    if (copied != null)
                    {
                        artifacts.Add(copied);
                    }
                }
                catch
                {
                    // Best effort only. We skip any candidate path that cannot be copied.
                }
            }

            if (artifacts.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var outputName = LooksLikeHtml(output) ? "output.html" : "output.txt";
                    var outputType = LooksLikeHtml(output) ? "text/html" : "text/plain";
                    var outputArtifact = WriteTextArtifact(folder, outputName, output, createdAt, outputType);
                    if (outputArtifact != null)
                    {
                        artifacts.Add(outputArtifact);
                    }
                }

                if (!string.IsNullOrWhiteSpace(log))
                {
                    var logArtifact = WriteTextArtifact(folder, "log.txt", log, createdAt, "text/plain");
                    if (logArtifact != null)
                    {
                        artifacts.Add(logArtifact);
                    }
                }
            }

            return artifacts;
        }

        private static ProgramArtifactRecord WriteTextArtifact(string folder, string fileName, string content, string createdAt, string contentType)
        {
            try
            {
                var path = EnsureUniqueFilePath(folder, fileName);
                File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
                return CreateArtifactRecord(path, createdAt, contentType);
            }
            catch
            {
                return null;
            }
        }

        private static ProgramArtifactRecord CreateArtifactRecord(string path, string createdAt, string contentType)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var file = new FileInfo(path);
            return new ProgramArtifactRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = file.Name,
                Path = file.FullName,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? GuessContentType(file.Extension) : contentType,
                SizeBytes = file.Length,
                CreatedAt = createdAt
            };
        }

        private static string CreateArtifactFolder(string jobId)
        {
            var safeJobId = SanitizeFileName(string.IsNullOrWhiteSpace(jobId) ? "unknown_job" : jobId);
            var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", "Jobs", safeJobId, "Artifacts");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static IEnumerable<string> FindArtifactFilePaths(params object[] roots)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots == null)
            {
                return results;
            }

            var visited = new HashSet<int>();
            foreach (var root in roots)
            {
                CollectArtifactFilePaths(root, 0, visited, results);
            }

            return results;
        }

        private static void CollectArtifactFilePaths(object value, int depth, HashSet<int> visited, HashSet<string> results)
        {
            if (value == null || depth > 4)
            {
                return;
            }

            if (value is string text)
            {
                TryAddArtifactPath(text, results);
                return;
            }

            if (value is Uri uri && uri.IsFile)
            {
                TryAddArtifactPath(uri.LocalPath, results);
                return;
            }

            if (!(value is string) && !value.GetType().IsValueType)
            {
                var id = RuntimeHelpers.GetHashCode(value);
                if (!visited.Add(id))
                {
                    return;
                }
            }

            if (value is IEnumerable sequence)
            {
                var count = 0;
                foreach (var item in sequence)
                {
                    CollectArtifactFilePaths(item, depth + 1, visited, results);
                    count++;
                    if (count >= 100)
                    {
                        break;
                    }
                }
            }

            foreach (var member in ArtifactPathMemberNames)
            {
                var memberValue = GetMemberValue(value, member);
                if (memberValue == null)
                {
                    continue;
                }

                CollectArtifactFilePaths(memberValue, depth + 1, visited, results);
            }
        }

        private static readonly string[] ArtifactPathMemberNames =
        {
            "Path",
            "FilePath",
            "FullPath",
            "LocalPath",
            "OutputPath",
            "ResultPath",
            "PdfPath",
            "HtmlPath",
            "ExcelPath",
            "XlsxPath",
            "Artifacts",
            "Results",
            "Result",
            "Output"
        };

        private static readonly HashSet<string> KnownArtifactExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".html",
            ".htm",
            ".pdf",
            ".xls",
            ".xlsx",
            ".csv",
            ".xml",
            ".txt",
            ".log",
            ".lst",
            ".rtf",
            ".json",
            ".ods"
        };

        private static void TryAddArtifactPath(string candidate, HashSet<string> results)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var path = candidate.Trim().Trim('"');
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
            }

            if (!File.Exists(path))
            {
                return;
            }

            var extension = Path.GetExtension(path);
            if (!string.IsNullOrWhiteSpace(extension) && !KnownArtifactExtensions.Contains(extension))
            {
                return;
            }

            results.Add(path);
        }

        private static bool LooksLikeHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("<table", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string EnsureUniqueFilePath(string folder, string fileName)
        {
            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? "artifact.bin" : fileName);
            var candidate = Path.Combine(folder, safeName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            var stem = Path.GetFileNameWithoutExtension(safeName);
            var ext = Path.GetExtension(safeName);
            for (var i = 1; i <= 5000; i++)
            {
                candidate = Path.Combine(folder, $"{stem}_{i}{ext}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{ext}");
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((name ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        }

        private static string GuessContentType(string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext == ".html" || ext == ".htm") return "text/html";
            if (ext == ".txt" || ext == ".log" || ext == ".lst") return "text/plain";
            if (ext == ".xml") return "application/xml";
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".xls") return "application/vnd.ms-excel";
            if (ext == ".xlsx") return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            if (ext == ".csv") return "text/csv";
            if (ext == ".rtf") return "application/rtf";
            if (ext == ".json") return "application/json";
            return "application/octet-stream";
        }

        private static ProgramCaptureFiles CreateProgramCaptureFiles(
            string jobId,
            SasServer server,
            string preferredServerLogPath,
            string preferredServerOutputPath)
        {
            var safeJobId = SanitizeFileName(string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString("N") : jobId);
            var folder = Path.Combine(Path.GetTempPath(), "SDVBridge", "Jobs", safeJobId, "Capture");
            Directory.CreateDirectory(folder);
            var localLogPath = Path.Combine(folder, "submit.log");
            var localOutputPath = Path.Combine(folder, "submit.lst");
            string remoteLogPath = null;
            string remoteOutputPath = null;
            var useConfiguredServerPaths = !string.IsNullOrWhiteSpace(preferredServerLogPath)
                                           && !string.IsNullOrWhiteSpace(preferredServerOutputPath);

            if (!useConfiguredServerPaths && server != null)
            {
                remoteLogPath = TryCreateRemoteCaptureFile(
                    server,
                    Path.Combine(folder, "seed_submit_log.log"),
                    "log");
                remoteOutputPath = TryCreateRemoteCaptureFile(
                    server,
                    Path.Combine(folder, "seed_submit_output.lst"),
                    "output");
            }

            var useTempServerFileref = IsLikelyServerFileref(remoteLogPath) && IsLikelyServerFileref(remoteOutputPath);
            var submitLogPath = useTempServerFileref
                ? TempLogFileref
                : (string.IsNullOrWhiteSpace(remoteLogPath) ? localLogPath : remoteLogPath);
            var submitOutputPath = useTempServerFileref
                ? TempOutputFileref
                : (string.IsNullOrWhiteSpace(remoteOutputPath) ? localOutputPath : remoteOutputPath);

            return new ProgramCaptureFiles
            {
                LogPath = localLogPath,
                OutputPath = localOutputPath,
                SubmitLogPath = useConfiguredServerPaths ? preferredServerLogPath : submitLogPath,
                SubmitOutputPath = useConfiguredServerPaths ? preferredServerOutputPath : submitOutputPath,
                UseTempServerFileref = useConfiguredServerPaths ? false : useTempServerFileref,
                UsesConfiguredServerPaths = useConfiguredServerPaths
            };
        }

        private static string WrapSubmittedCode(string originalCode, ProgramCaptureFiles captureFiles)
        {
            if (captureFiles == null)
            {
                return originalCode ?? string.Empty;
            }

            var userCode = originalCode ?? string.Empty;
            if (captureFiles.UseTempServerFileref)
            {
                var sbFileref = new StringBuilder();
                sbFileref.AppendLine($"filename {captureFiles.SubmitLogPath} temp;");
                sbFileref.AppendLine($"filename {captureFiles.SubmitOutputPath} temp;");
                sbFileref.AppendLine($"proc printto log={captureFiles.SubmitLogPath} print={captureFiles.SubmitOutputPath} new;");
                sbFileref.AppendLine("run;");
                sbFileref.AppendLine(userCode);
                sbFileref.AppendLine("proc printto;");
                sbFileref.AppendLine("run;");
                return sbFileref.ToString();
            }

            var logRef = TempLogFileref;
            var outRef = TempOutputFileref;
            var sb = new StringBuilder();
            sb.AppendLine($"filename {logRef} {ToSasStringLiteral(captureFiles.SubmitLogPath)};");
            sb.AppendLine($"filename {outRef} {ToSasStringLiteral(captureFiles.SubmitOutputPath)};");
            sb.AppendLine($"proc printto log={logRef} print={outRef} new;");
            sb.AppendLine("run;");
            sb.AppendLine(userCode);
            sb.AppendLine("proc printto;");
            sb.AppendLine("run;");
            sb.AppendLine($"filename {logRef} clear;");
            sb.AppendLine($"filename {outRef} clear;");
            return sb.ToString();
        }

        private static bool IsLikelyServerFileref(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            return text.IndexOfAny(new[] { '\\', '/', ':', ' ', '\t', '\r', '\n', '"' }) < 0;
        }

        private static string NormalizeOptionalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return path.Trim();
        }

        private static string ToSasStringLiteral(string value)
        {
            var text = value ?? string.Empty;
            if (text.IndexOf('%') >= 0 || text.IndexOf('&') >= 0)
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }

            return "'" + text.Replace("'", "''") + "'";
        }

        private static string TryReadFileText(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static SasServer ResolveSasServer(string preferredServerName)
        {
            try
            {
                var servers = SasServer.GetSasServers();
                if (servers == null || servers.Count == 0)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(preferredServerName))
                {
                    return servers[0];
                }

                return servers.FirstOrDefault(s => string.Equals(s.Name, preferredServerName, StringComparison.OrdinalIgnoreCase))
                       ?? servers.FirstOrDefault(s => string.Equals(s.DisplayName, preferredServerName, StringComparison.OrdinalIgnoreCase))
                       ?? servers[0];
            }
            catch (Exception ex)
            {
                Log($"ResolveSasServer failed: {ex.Message}");
                return null;
            }
        }

        private static string TryCreateRemoteCaptureFile(SasServer server, string localSeedPath, string label)
        {
            if (server == null || string.IsNullOrWhiteSpace(localSeedPath))
            {
                return null;
            }

            try
            {
                var folder = Path.GetDirectoryName(localSeedPath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(localSeedPath, string.Empty, Encoding.UTF8);
                var remotePath = server.CopyLocalFileToServer(localSeedPath);
                if (!string.IsNullOrWhiteSpace(remotePath))
                {
                    return remotePath.Trim().Trim('"');
                }

                Log($"CopyLocalFileToServer returned empty remote path for {label} seed '{localSeedPath}'.");
            }
            catch (Exception ex)
            {
                Log($"CopyLocalFileToServer failed for {label} seed '{localSeedPath}': {ex.Message}");
            }

            return null;
        }

        private static void DownloadServerCaptureFiles(ProgramCaptureFiles captureFiles, SasServer server)
        {
            if (captureFiles == null || !captureFiles.UsesServerCapture || server == null)
            {
                return;
            }

            var logDownloaded = TryDownloadServerCaptureFile(server, captureFiles.SubmitLogPath, ".log", captureFiles.LogPath);
            Log($"Server log download {(logDownloaded ? "succeeded" : "failed")} for remote '{captureFiles.SubmitLogPath}'.");

            var outputDownloaded = TryDownloadServerCaptureFile(server, captureFiles.SubmitOutputPath, ".lst", captureFiles.OutputPath);
            Log($"Server output download {(outputDownloaded ? "succeeded" : "failed")} for remote '{captureFiles.SubmitOutputPath}'.");
        }

        private static bool TryDownloadServerCaptureFile(SasServer server, string remotePath, string extension, string localTargetPath)
        {
            if (server == null || string.IsNullOrWhiteSpace(remotePath) || string.IsNullOrWhiteSpace(localTargetPath))
            {
                return false;
            }

            var targetFolder = Path.GetDirectoryName(localTargetPath);
            if (!string.IsNullOrWhiteSpace(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
            }

            var normalizedExt = string.IsNullOrWhiteSpace(extension)
                ? ".tmp"
                : (extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension);

            var maxWait = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < maxWait)
            {
                var copiedPath = string.Empty;
                try
                {
                    copiedPath = server.CopyServerFileToLocal(remotePath, normalizedExt);
                }
                catch
                {
                    try
                    {
                        copiedPath = server.CopyServerFileToLocal(
                            remotePath,
                            Path.GetFileNameWithoutExtension(localTargetPath) ?? "sdvbridge_capture",
                            normalizedExt);
                    }
                    catch
                    {
                        copiedPath = null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(copiedPath))
                {
                    copiedPath = copiedPath.Trim().Trim('"');
                    if (File.Exists(copiedPath))
                    {
                        if (!string.Equals(copiedPath, localTargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(copiedPath, localTargetPath, true);
                        }

                        if (File.Exists(localTargetPath))
                        {
                            return true;
                        }
                    }
                }
                else if (File.Exists(localTargetPath))
                {
                    return true;
                }

                Thread.Sleep(250);
            }

            return File.Exists(localTargetPath);
        }

        private object InvokeSubmit(
            string code,
            string requestedServer,
            object submitter,
            object consumer,
            Action<object, object, object> progressTick)
        {
            var server = string.IsNullOrWhiteSpace(requestedServer)
                ? (GetMemberValue(consumer, "AssignedServer") as string)
                : requestedServer;

            Exception lastError = null;
            foreach (var target in new[] { submitter, consumer })
            {
                if (target == null)
                {
                    continue;
                }

                if (TryInvokeSubmitOnTarget(target, code, server, consumer, progressTick, out var result, out var error))
                {
                    return result;
                }

                if (error != null)
                {
                    lastError = error;
                }
            }

            if (lastError != null)
            {
                throw lastError;
            }

            throw new InvalidOperationException("Unable to find a submit API on the current EG consumer.");
        }

        private static bool TryInvokeSubmitOnTarget(
            object target,
            string code,
            string server,
            object consumer,
            Action<object, object, object> progressTick,
            out object result,
            out Exception error)
        {
            result = null;
            error = null;
            var type = target.GetType();
            var methods = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => SubmitMethodNames.Any(name => string.Equals(name, m.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => SubmitMethodRank(m.Name))
                .ThenBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var method in methods)
            {
                if (!TryBuildSubmitArgs(method, code, server, consumer, out var args))
                {
                    continue;
                }

                try
                {
                    result = method.Invoke(target, args);
                    Log($"Submit API selected: targetType={target.GetType().FullName}, method={FormatMethodSignature(method)}, resultType={(result == null ? "<null>" : result.GetType().FullName)}");
                    WaitForSubmitCompletion(target, consumer, method, result, progressTick);

                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    error = ex.InnerException ?? ex;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }

            return false;
        }

        private static void WaitForSubmitCompletion(
            object target,
            object consumer,
            MethodInfo submitMethod,
            object submitResult,
            Action<object, object, object> progressTick)
        {
            progressTick?.Invoke(submitResult, target, consumer);

            if (submitResult is IAsyncResult asyncResult)
            {
                WaitForAsyncResult(asyncResult, submitResult, target, consumer, progressTick);
                progressTick?.Invoke(submitResult, target, consumer);
                return;
            }

            if (submitMethod == null)
            {
                return;
            }

            if (submitMethod.Name.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) >= 0 ||
                submitMethod.Name.IndexOf("Synchronous", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            if (!LooksLikeRunning(target, consumer))
            {
                return;
            }

            var maxWait = DateTime.UtcNow.AddHours(12);
            while (DateTime.UtcNow < maxWait)
            {
                progressTick?.Invoke(submitResult, target, consumer);
                Thread.Sleep(300);

                if (TryGetCompletionState(out var completed, target, consumer) && completed)
                {
                    break;
                }
            }

            progressTick?.Invoke(submitResult, target, consumer);
        }

        private static void WaitForAsyncResult(
            IAsyncResult asyncResult,
            object submitResult,
            object submitTarget,
            object consumer,
            Action<object, object, object> progressTick)
        {
            if (asyncResult == null)
            {
                return;
            }

            var maxWait = DateTime.UtcNow.AddHours(12);
            while (!asyncResult.IsCompleted && DateTime.UtcNow < maxWait)
            {
                progressTick?.Invoke(submitResult, submitTarget, consumer);

                var waitHandle = asyncResult.AsyncWaitHandle;
                if (waitHandle != null)
                {
                    waitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                }
                else
                {
                    Thread.Sleep(300);
                }
            }
        }

        private static bool LooksLikeRunning(params object[] candidates)
        {
            return TryGetCompletionState(out var completed, candidates) && !completed;
        }

        private static bool TryGetCompletionState(out bool completed, params object[] candidates)
        {
            completed = false;
            if (candidates == null)
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                foreach (var name in CompletionMemberNames)
                {
                    if (!TryGetBoolMember(candidate, name, out var value))
                    {
                        continue;
                    }

                    completed = value;
                    return true;
                }

                foreach (var name in BusyMemberNames)
                {
                    if (!TryGetBoolMember(candidate, name, out var value))
                    {
                        continue;
                    }

                    completed = !value;
                    return true;
                }

                foreach (var name in StatusMemberNames)
                {
                    var statusText = GetMemberValue(candidate, name) as string;
                    if (string.IsNullOrWhiteSpace(statusText))
                    {
                        var raw = GetMemberValue(candidate, name);
                        statusText = raw == null ? null : raw.ToString();
                    }

                    if (string.IsNullOrWhiteSpace(statusText))
                    {
                        continue;
                    }

                    var normalized = statusText.Trim().ToLowerInvariant();
                    if (normalized.Contains("complete") || normalized.Contains("done") || normalized.Contains("finish"))
                    {
                        completed = true;
                        return true;
                    }

                    if (normalized.Contains("fail") || normalized.Contains("error") || normalized.Contains("cancel"))
                    {
                        completed = true;
                        return true;
                    }

                    if (normalized.Contains("run") || normalized.Contains("busy") || normalized.Contains("progress") || normalized.Contains("submit"))
                    {
                        completed = false;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetBoolMember(object target, string memberName, out bool value)
        {
            value = false;
            var raw = GetMemberValue(target, memberName);
            if (raw == null)
            {
                return false;
            }

            if (raw is bool asBool)
            {
                value = asBool;
                return true;
            }

            if (bool.TryParse(raw.ToString(), out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static int SubmitMethodRank(string methodName)
        {
            for (var i = 0; i < SubmitMethodNames.Length; i++)
            {
                if (string.Equals(SubmitMethodNames[i], methodName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            if (method == null)
            {
                return "<null>";
            }

            var parameters = method.GetParameters();
            var parts = parameters
                .Select(p => $"{p.ParameterType.Name} {p.Name}")
                .ToArray();
            return $"{method.Name}({string.Join(", ", parts)})";
        }

        private static bool TryBuildSubmitArgs(
            MethodInfo method,
            string code,
            string server,
            object consumer,
            out object[] args)
        {
            var parameters = method.GetParameters();
            args = new object[parameters.Length];

            var usedCode = false;
            var usedServer = false;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.ParameterType.IsByRef)
                {
                    return false;
                }

                var value = ResolveParameterValue(
                    parameter,
                    code,
                    server,
                    consumer,
                    ref usedCode,
                    ref usedServer,
                    out var resolved);
                if (!resolved)
                {
                    return false;
                }

                args[i] = value;
            }

            return true;
        }

        private static object ResolveParameterValue(
            ParameterInfo parameter,
            string code,
            string server,
            object consumer,
            ref bool usedCode,
            ref bool usedServer,
            out bool resolved)
        {
            resolved = true;
            var parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
            var parameterType = parameter.ParameterType;

            if (parameterType == typeof(string))
            {
                if (parameterName.Contains("code") || parameterName.Contains("program"))
                {
                    usedCode = true;
                    return code ?? string.Empty;
                }

                if (parameterName.Contains("server") || parameterName.Contains("workspace"))
                {
                    usedServer = true;
                    return server ?? string.Empty;
                }

                if (!usedCode)
                {
                    usedCode = true;
                    return code ?? string.Empty;
                }

                if (!usedServer)
                {
                    usedServer = true;
                    return server ?? string.Empty;
                }

                if (parameter.IsOptional)
                {
                    return Type.Missing;
                }

                return string.Empty;
            }

            if (parameterType == typeof(string[]))
            {
                usedCode = true;
                return new[] { code ?? string.Empty };
            }

            if (parameterType == typeof(bool))
            {
                if (parameterName.Contains("wait"))
                {
                    return true;
                }

                return parameter.IsOptional ? Type.Missing : (object)false;
            }

            if (parameter.IsOptional)
            {
                return Type.Missing;
            }

            if (consumer != null && parameterType.IsInstanceOfType(consumer))
            {
                return consumer;
            }

            if (!parameterType.IsValueType)
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(parameterType);
            }
            catch
            {
                resolved = false;
                return null;
            }
        }

        private static string TryReadText(object root, string[] memberNames)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var memberName in memberNames)
            {
                var value = GetMemberValue(root, memberName);
                if (value == null)
                {
                    continue;
                }

                if (TryCoerceToText(value, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (TryCoerceToText(root, out var directText) && !string.IsNullOrWhiteSpace(directText))
            {
                return directText;
            }

            return null;
        }

        private static bool TryCoerceToText(object value, out string text)
        {
            return TryCoerceToText(value, out text, 0, new HashSet<int>());
        }

        private static bool TryCoerceToText(object value, out string text, int depth, HashSet<int> visited)
        {
            text = null;
            if (value == null || depth > 4)
            {
                return false;
            }

            if (!(value is string) && !value.GetType().IsValueType)
            {
                var id = RuntimeHelpers.GetHashCode(value);
                if (!visited.Add(id))
                {
                    return false;
                }
            }

            if (value is string str)
            {
                text = str;
                return true;
            }

            if (value is byte[] bytes)
            {
                text = Encoding.UTF8.GetString(bytes);
                return true;
            }

            if (value is IEnumerable<string> lines)
            {
                text = string.Join(Environment.NewLine, lines);
                return true;
            }

            if (value is IEnumerable sequence)
            {
                var buffer = new List<string>();
                var count = 0;
                foreach (var item in sequence)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (TryCoerceToText(item, out var line, depth + 1, visited) && !string.IsNullOrWhiteSpace(line))
                    {
                        buffer.Add(line);
                    }

                    count++;
                    if (count >= 200)
                    {
                        break;
                    }
                }

                if (buffer.Count > 0)
                {
                    text = string.Join(Environment.NewLine, buffer);
                    return true;
                }
            }

            foreach (var nestedName in new[]
            {
                "Text",
                "Value",
                "Contents",
                "Xml",
                "XmlResult",
                "Output",
                "RunLog",
                "RunLogText",
                "LogText",
                "Log",
                "Messages",
                "MessageText",
                "Result",
                "Results",
                "Listing"
            })
            {
                var nested = GetMemberValue(value, nestedName);
                if (nested == null)
                {
                    continue;
                }

                if (TryCoerceToText(nested, out text, depth + 1, visited) && !string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }
            }

            if (ShouldSkipToStringFallback(value))
            {
                return false;
            }

            var toStringMethod = value.GetType().GetMethod("ToString", Type.EmptyTypes);
            if (toStringMethod != null && toStringMethod.DeclaringType != typeof(object))
            {
                text = value.ToString();
                return !string.IsNullOrWhiteSpace(text);
            }

            return false;
        }

        private static bool ShouldSkipToStringFallback(object value)
        {
            if (value == null)
            {
                return true;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }

            return value is decimal
                   || value is DateTime
                   || value is DateTimeOffset
                   || value is TimeSpan
                   || value is Guid;
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = target.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            var method = type
                .GetMethods(flags)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 0 &&
                    m.ReturnType != typeof(void));
            if (method != null)
            {
                try
                {
                    return method.Invoke(target, null);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private ProgramJobRecord FindJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            lock (_syncRoot)
            {
                if (!_jobs.TryGetValue(jobId, out var record))
                {
                    return null;
                }

                return Clone(record);
            }
        }

        private void UpdateJob(string jobId, Action<ProgramJobRecord> updater)
        {
            if (string.IsNullOrWhiteSpace(jobId) || updater == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (!_jobs.TryGetValue(jobId, out var record))
                {
                    return;
                }

                var mutable = Clone(record);
                updater(mutable);
                _jobs[jobId] = mutable;
            }
        }

        private void StoreJob(ProgramJobRecord record)
        {
            if (record == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (!_jobs.ContainsKey(record.JobId))
                {
                    _jobOrder.Enqueue(record.JobId);
                }

                _jobs[record.JobId] = Clone(record);
                while (_jobOrder.Count > 200)
                {
                    var oldest = _jobOrder.Dequeue();
                    _jobs.Remove(oldest);
                }
            }
        }

        private static ProgramJobRecord Clone(ProgramJobRecord source)
        {
            if (source == null)
            {
                return null;
            }

            return new ProgramJobRecord
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
                    ? new List<ProgramArtifactRecord>()
                    : source.Artifacts.Select(CloneArtifactRecord).ToList()
            };
        }

        private static ProgramArtifactRecord CloneArtifactRecord(ProgramArtifactRecord source)
        {
            if (source == null)
            {
                return null;
            }

            return new ProgramArtifactRecord
            {
                Id = source.Id,
                Name = source.Name,
                Path = source.Path,
                ContentType = source.ContentType,
                SizeBytes = source.SizeBytes,
                CreatedAt = source.CreatedAt
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static void Log(string message)
        {
            SDVBridgeLog.Info($"[SasProgramService] {message}");
        }

        private sealed class ProgramCaptureFiles
        {
            public string LogPath { get; set; }

            public string OutputPath { get; set; }

            public string SubmitLogPath { get; set; }

            public string SubmitOutputPath { get; set; }

            public bool UseTempServerFileref { get; set; }

            public bool UsesConfiguredServerPaths { get; set; }

            public bool UsesServerCapture
            {
                get
                {
                    return !string.IsNullOrWhiteSpace(SubmitLogPath)
                           && !string.IsNullOrWhiteSpace(SubmitOutputPath)
                           && (!string.Equals(SubmitLogPath, LogPath, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(SubmitOutputPath, OutputPath, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        private sealed class ProgramExecutionResult
        {
            public string Status { get; set; }

            public string Error { get; set; }

            public string Log { get; set; }

            public string Output { get; set; }

            public List<ProgramArtifactRecord> Artifacts { get; set; }

            public static ProgramExecutionResult Completed(string log, string output, List<ProgramArtifactRecord> artifacts)
            {
                return new ProgramExecutionResult
                {
                    Status = "completed",
                    Log = log,
                    Output = output,
                    Artifacts = artifacts ?? new List<ProgramArtifactRecord>()
                };
            }

            public static ProgramExecutionResult Failed(string error, string log, string output, List<ProgramArtifactRecord> artifacts)
            {
                return new ProgramExecutionResult
                {
                    Status = "failed",
                    Error = error,
                    Log = log,
                    Output = output,
                    Artifacts = artifacts ?? new List<ProgramArtifactRecord>()
                };
            }
        }
    }
}
