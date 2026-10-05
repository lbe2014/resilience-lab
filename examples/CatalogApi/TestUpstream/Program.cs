var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5111");
var app = builder.Build();
var gate = new object();
var mode = "healthy";
var attempts = 0;
var active = 0;
var canceled = 0;
app.MapGet("/health", () => Results.Ok());
app.MapPost("/mode/{value}", (string value) =>
{
    if (value is not ("healthy" or "flaky" or "slow" or "down" or "invalid" or "missing")) return Results.BadRequest();
    lock (gate) { mode = value; attempts = 0; canceled = 0; }
    return Results.Ok();
});
app.MapGet("/stats", () => { lock (gate) return Results.Ok(new { attempts, active, canceled }); });
app.MapGet("/products", async (CancellationToken token) =>
{
    string current;
    int attempt;
    lock (gate) { current = mode; attempt = ++attempts; active++; }
    try
    {
        if (current == "slow") await Task.Delay(5000, token);
        if (current == "down" || current == "flaky" && attempt < 3) return Results.StatusCode(503);
        if (current == "missing") return Results.StatusCode(404);
        if (current == "invalid") return Results.Text("broken-json", "application/json");
        return Results.Json(new { products = new[] { new { id = 1, title = "Catálogo de prueba", price = 42.50m } }, total = 1 });
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
        lock (gate) canceled++;
        throw;
    }
    finally { lock (gate) active--; }
});
app.Run();
