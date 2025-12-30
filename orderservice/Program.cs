using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using MySqlConnector;
using Dapper;
using System.Diagnostics;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseNLog();

// ActivitySource for DB operations (exported by OpenTelemetry)
var dbActivitySource = new ActivitySource("orderservice-db");

// connection: read from configuration or env, fallback to docker service name
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("MYSQL_CONNECTION");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("orderservice-db")
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("orderservice"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))));

builder.Services.AddHttpClient();
builder.Services.AddSingleton(dbActivitySource);
builder.Services.AddScoped(_ => new MySqlConnection(connectionString));

var app = builder.Build();

app.MapGet("/order", async (HttpClient client, ILogger<Program> logger, ActivitySource dbActivitySource, MySqlConnection connection) =>
{
    try
    {
        // Parse connection info for tags (avoid leaking password)
        var csb = new MySqlConnectionStringBuilder(connectionString);

        var insertSql = "INSERT INTO `Orders` (CreatedAt, Description) VALUES (@CreatedAt, @Description); SELECT LAST_INSERT_ID();";

        using var activity = dbActivitySource.StartActivity("mysql.query", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", "mysql");
            activity.SetTag("db.name", csb.Database);
            activity.SetTag("net.peer.name", csb.Server);
            activity.SetTag("db.user", csb.UserID);
            // we will set the statement before executing each command
        }

        if (connection.State != System.Data.ConnectionState.Open)
        await connection.OpenAsync();

        if (activity is not null) activity.SetTag("db.statement", insertSql);
        var orderId = await connection.ExecuteScalarAsync<int>(insertSql,
            new { CreatedAt = DateTime.UtcNow, Description = "New Order" });

        logger.LogInformation("Created order with ID: {OrderId}", orderId);
        
        if (activity is not null) activity.SetTag("db.order_id", orderId);

        var pay = await client.GetStringAsync(Environment.GetEnvironmentVariable("PAYMENTSERVICE_ENDPOINT"));
        
        logger.LogInformation("Payment service response: {Response}", pay);
        return $"Order ID: {orderId}";
    }
    catch (Exception ex)
    {
        // record error on current activity if present
        var a = Activity.Current;
        if (a is not null)
        {
            a.SetTag("otel.status_code", "ERROR");
            a.SetTag("otel.status_description", ex.Message);
        }
        // Also return a concise error
        return $"Error creating order: {ex.Message}";
    }
});

app.Run();