using MockServer;
using MockServer.Models;

var port = ResolvePort(args);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = LowerCaseNamingPolicy.Instance;
    options.SerializerOptions.DictionaryKeyPolicy = LowerCaseNamingPolicy.Instance;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
app.UseCors();
app.Urls.Clear();
app.Urls.Add($"http://127.0.0.1:{port}");

var store = new MockDataStore();

app.MapGet("/servers", () => Results.Json(store.GetServers()));

app.MapGet("/servers/{server}/libraries", (string server) =>
{
    if (!store.TryGetLibraries(server, out var libraries))
    {
        return Results.NotFound(new { error = $"Unknown server '{server}'." });
    }

    return Results.Json(libraries);
});

app.MapGet("/servers/{server}/libraries/{libref}/datasets", (string server, string libref) =>
{
    if (!store.TryGetDatasets(server, libref, out var datasets))
    {
        return Results.NotFound(new { error = $"No datasets found for {server}/{libref}." });
    }

    return Results.Json(datasets);
});

app.MapGet("/servers/{server}/libraries/{libref}/datasets/{member}/columns", (string server, string libref, string member) =>
{
    if (!store.TryGetColumns(server, libref, member, out var columns))
    {
        return Results.NotFound(new { error = $"No columns found for {server}/{libref}/{member}." });
    }

    return Results.Json(columns);
});

app.MapGet("/servers/{server}/libraries/{libref}/datasets/{member}/preview", (string server, string libref, string member, HttpRequest request) =>
{
    var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 0;
    if (!store.TryGetPreview(server, libref, member, limit, out var preview))
    {
        return Results.NotFound(new { error = $"No preview found for {server}/{libref}/{member}." });
    }

    return Results.Json(preview);
});

app.MapPost("/datasets/open", (DatasetOpenRequest? request) =>
{
    if (request == null)
    {
        return Results.BadRequest(new { error = "Request body is required." });
    }

    try
    {
        var export = store.CreateDataset(request);
        return Results.Json(new { path = export.LocalPath, filename = export.FileName });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/programs/submit", (ProgramSubmitRequest? request) =>
{
    if (request == null)
    {
        return Results.BadRequest(new { error = "Request body is required." });
    }

    try
    {
        return Results.Json(store.SubmitProgram(request));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/programs/submit/async", (ProgramSubmitRequest? request) =>
{
    if (request == null)
    {
        return Results.BadRequest(new { error = "Request body is required." });
    }

    try
    {
        var response = store.QueueProgram(request);
        return Results.Json(response, statusCode: StatusCodes.Status202Accepted);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/jobs/{jobId}", (string jobId) =>
{
    if (!store.TryGetJobStatus(jobId, out var response))
    {
        return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
    }

    return Results.Json(response);
});

app.MapGet("/jobs/{jobId}/artifacts", (string jobId) =>
{
    if (!store.TryGetJobArtifacts(jobId, out var response))
    {
        return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
    }

    return Results.Json(response);
});

app.MapGet("/jobs/{jobId}/artifacts/{artifactId}", (string jobId, string artifactId) =>
{
    if (!store.TryGetJobArtifact(jobId, artifactId, out var artifact))
    {
        return Results.NotFound(new { error = $"Artifact '{artifactId}' was not found for job '{jobId}'." });
    }

    return Results.File(
        artifact.Path,
        artifact.ContentType ?? "application/octet-stream",
        fileDownloadName: artifact.Name);
});

app.MapGet("/jobs/{jobId}/log", (string jobId, HttpRequest request) =>
{
    var offset = int.TryParse(request.Query["offset"], out var parsedOffset) ? parsedOffset : 0;
    if (!store.TryGetJobLog(jobId, offset, out var response))
    {
        return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
    }

    return Results.Json(response);
});

app.MapGet("/jobs/{jobId}/output", (string jobId) =>
{
    if (!store.TryGetJobOutput(jobId, out var response))
    {
        return Results.NotFound(new { error = $"Job '{jobId}' was not found." });
    }

    return Results.Json(response);
});

app.MapGet("/", () => Results.Json(new
{
    status = "ok",
    message = "SDVBridge mock API is running.",
    endpoints = new[]
    {
        "/servers",
        "/servers/{server}/libraries",
        "/servers/{server}/libraries/{libref}/datasets",
        "/servers/{server}/libraries/{libref}/datasets/{member}/columns",
        "/servers/{server}/libraries/{libref}/datasets/{member}/preview",
        "/datasets/open",
        "/programs/submit",
        "/programs/submit/async",
        "/jobs/{jobid}",
        "/jobs/{jobid}/artifacts",
        "/jobs/{jobid}/artifacts/{artifactid}",
        "/jobs/{jobid}/log",
        "/jobs/{jobid}/output"
    }
}));

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Mock SDVBridge server listening on http://127.0.0.1:{port}/");
    Console.WriteLine("Press Ctrl+C to stop.");
});

await app.RunAsync();

static int ResolvePort(string[] args)
{
    const int defaultPort = 17832;
    for (int i = 0; i < args.Length; i++)
    {
        if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
        {
            if (parsed > 0 && parsed <= 65535)
            {
                return parsed;
            }
        }
    }

    return defaultPort;
}
