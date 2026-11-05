// HTTP host that runs one AgentDojo task per request, with or without Provenance.
// The Python bridge (../provenance_agentdojo) sends the task and the URL of an MCP server that
// exposes the task's tools; this host runs the agent against those tools and returns the answer.
//
// Run with a local Ollama model:
//   dotnet run --project benchmarks/agentdojo/host -- --Model:Name=qwen3:8b
// Run the plumbing check, with no model at all:
//   dotnet run --project benchmarks/agentdojo/host -- --Model:Provider=scripted

using Provenance.AgentDojo.Host;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5080");

var model = ModelSettings.From(builder.Configuration);
var policies = SuitePolicies.Load(Path.Combine(AppContext.BaseDirectory, "policies"));

var app = builder.Build();

app.MapGet("/", () => $"Provenance AgentDojo host. Model: {model}. Policies: {string.Join(", ", policies.Suites)}.");

app.MapPost("/run", async (RunRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await AgentRunner.RunAsync(request, model, policies, cancellationToken));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Report the failure to the bridge instead of failing the HTTP call, so one bad task
        // doesn't abort a long benchmark run.
        return Results.Ok(new RunResponse(null, [], $"{ex.GetType().Name}: {ex.Message}"));
    }
});

app.Logger.LogInformation("Model: {Model}", model);
app.Run();
