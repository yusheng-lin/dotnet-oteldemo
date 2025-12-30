using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseNLog();

// 關鍵 8 行 OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("gateway"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => { opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")); }));

builder.Services.AddHttpClient();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/create-order", async (HttpClient client, ILogger<Program> logger) =>
{
    var result = await client.GetStringAsync(Environment.GetEnvironmentVariable("ORDERSERVICE_ENDPOINT"));
    logger.LogInformation("Order service response: {Response}", result);
    return Results.Ok(new { message = "Order created", detail = result });
});

app.Run();