using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseNLog();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("paymentservice"))
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("JAEGER_ENDPOINT"))));

var app = builder.Build();

app.MapGet("/pay", async (ILogger<Program> logger) =>
{
    await Task.Delay(Random.Shared.Next(80, 200));

    logger.LogInformation("Payment completed");

    return "Payment completed";
});

app.Run();