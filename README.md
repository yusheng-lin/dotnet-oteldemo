# dotnet-oteldemo

Lightweight demo showing .NET services with OpenTelemetry -> Jaeger (OTLP) and a simple orders flow persisted to MySQL.

This repository contains three services wired with OpenTelemetry and Jaeger collector in `docker-compose.yml`:

- `gateway`  (ports: host 5000 -> container 8080)
- `orderservice` (ports: none exposed; called by gateway)
- `paymentservice` (ports: none exposed; called by orderservice)
- `jaeger` (UI: http://localhost:16686, OTLP gRPC: 4317, OTLP HTTP: 4318)
- `mysql` (added for demo persistence, default port 3306)

What this README covers
- How to build and run the demo (docker-compose)
- How to generate traffic and verify traces in Jaeger
- How DB tracing is implemented and how to find DB spans
- Troubleshooting

Requirements
- Docker & Docker Compose
- .NET 8 SDK (for local builds if you want to `dotnet build`)

Run the demo (recommended)

1. From the repository root, build & start containers:

```bash
docker compose up -d --build
```

2. Generate traffic (gateway exposes a convenience endpoint):

```bash
# Create an order via the gateway
curl -sS http://localhost:5000/create-order

# Or call the orderservice directly (inside compose network):
curl -sS http://localhost:5000/create-order
```

3. Open Jaeger UI

Visit http://localhost:16686. Use the Service dropdown to select `gateway`, `orderservice`, or `paymentservice`.

DB tracing and where to find SQL spans

- The `orderservice` uses a manual ActivitySource named `orderservice-db` to create DB spans around SQL operations. The activity name used by the code is `mysql.query`.
- The TracerProvider is configured to include that ActivitySource (so manual DB activities become spans) — look for `.AddSource("orderservice-db")` in `orderservice/Program.cs`.
- DB activities include standard tags such as `db.system` (`mysql`), `db.name`, `net.peer.name`, `db.user`, and `db.statement`. The code sets `db.statement` before executing the SQL, so you should see the `CREATE TABLE` and `INSERT` SQL strings on the DB spans in Jaeger (note: Jaeger UI may truncate long statements).

MySQL details

- The compose service creates a MySQL container with:
  - user: `otel`
  - password: `otelpass`
  - database: `ordersdb`
  - port 3306 (host:container mapping `3306:3306`)
- The `orderservice` default connection string (used by the demo) is:

```
server=mysql;port=3306;user=otel;password=otelpass;database=ordersdb
```

Notes on case-sensitivity and table names

- MySQL table names are case-sensitive on some platforms/hosts (depending on `lower_case_table_names` and filesystem). The demo creates and inserts into a table named ``Orders`` (backticks used) to avoid accidental case issues. If you manually created the table, ensure the exact name and casing matches.

How DB tracing is emitted (brief)

- The code in `orderservice/Program.cs`:
  - Creates an ActivitySource `orderservice-db`.
  - Starts an Activity named `mysql.query` with kind `Client` before running SQL.
  - Adds standard tags (db.system, db.name, db.user, db.statement, etc.) to the activity.
  - Saves/executes SQL using `MySqlConnector` + `Dapper`.
  - The TracerProvider registers `.AddSource("orderservice-db")`, so these activities are collected and exported to Jaeger via OTLP.

If you don't see DB spans in Jaeger

1. Confirm services are up and healthy:

```bash
docker compose ps
docker compose logs jaeger --tail=200
docker compose logs orderservice --tail=200
```

2. Generate requests (repeat a few times). Then in Jaeger UI:
  - Set the Time Range to the last minute(s).
  - Select `orderservice` in the Service field and click `Find Traces`.
  - Look at individual traces — DB spans will appear as child spans of the request span and should be named `mysql.query`.

3. If you see request spans but no `mysql.query` spans:
  - Confirm `orderservice` has `.AddSource("orderservice-db")` in `Program.cs`.
  - Confirm `orderservice` code starts activities using the same source name: `new ActivitySource("orderservice-db")`.
  - Check `orderservice` logs for exceptions (export errors or SQL errors). Example:

```bash
docker compose logs orderservice --tail=200
```

4. If SQL statements are failing with an error like `"ordersdb.orders' doesn't exist"`:
  - Confirm you created the table in the correct database and with the same casing. The demo creates ``Orders`` with backticks; verify with the MySQL client.
  - Connect to the MySQL container and inspect tables:

```bash
docker compose exec mysql mysql -u root -p
# enter password: otelpass
USE ordersdb;
SHOW TABLES;
DESCRIBE `Orders`;
```

Advanced: change exporter protocol

- The services send OTLP to Jaeger at `http://jaeger:4317` (gRPC). If you want to use OTLP HTTP instead, set the endpoint to `http://jaeger:4318/v1/traces` and ensure the exporter is configured for HTTP protobuf.

Final notes

- The demo intentionally uses a manual ActivitySource for DB tracing to keep dependencies small and avoid EF/Core version issues.
- If you'd like, I can:
  - Add a small script to continuously generate load and create many DB spans.
  - Add a health-check endpoint for MySQL readiness.
  - Add a Docker healthcheck for the MySQL service.

If you want changes to the README (formatting, extra commands, or adding screenshots), tell me what to include and I'll update it.
