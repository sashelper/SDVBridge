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

app.MapGet("/", () => Results.Json(new
{
    status = "ok",
    message = "SDVBridge mock API is running.",
    endpoints = new[]
    {
        "/servers",
        "/servers/{server}/libraries",
        "/servers/{server}/libraries/{libref}/datasets",
        "/datasets/open"
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
