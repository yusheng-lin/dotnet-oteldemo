using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// 關鍵 8 行 OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("gateway"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => { opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("JAEGER_ENDPOINT")); }));

builder.Services.AddHttpClient();

var app = builder.Build();

app.MapGet("/create-order", async (HttpClient client) =>
{
    var result = await client.GetStringAsync(Environment.GetEnvironmentVariable("ORDERSERVICE_ENDPOINT"));
    return Results.Ok(new { message = "Order created", detail = result });
});

app.Run();