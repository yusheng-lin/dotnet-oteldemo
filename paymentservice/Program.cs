using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("paymentservice"))
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("JAEGER_ENDPOINT"))));

var app = builder.Build();

app.MapGet("/pay", async () =>
{
    await Task.Delay(Random.Shared.Next(80, 200));
    return "Payment completed";
});

app.Run();