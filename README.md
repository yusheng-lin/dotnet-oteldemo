# dotnet-oteldemo

Lightweight demo showing .NET services with OpenTelemetry -> Jaeger (OTLP) and a simple orders flow persisted to MySQL.

This repository contains three services wired with OpenTelemetry and Jaeger collector in `docker-compose.yml`:

- `gateway`  (ports: host 5000 -> container 8080)
- `orderservice` (ports: none exposed; called by gateway)
- `paymentservice` (ports: none exposed; called by orderservice)
- `jaeger` (UI: http://localhost:16686, OTLP gRPC: 4317, OTLP HTTP: 4318)
- `mysql` (added for demo persistence, default port 3306)
- `kafka` (Message broker using KRaft mode - no ZooKeeper required)

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
curl -s http://localhost:5000/create-order

# Or call the orderservice directly (inside compose network):
curl -s http://localhost:5000/create-order
```

3. Open Jaeger UI

Visit http://localhost:16686. Use the Service dropdown to select `gateway`, `orderservice`, or `paymentservice`.

Logging flow (NLog → Kafka → Logstash → Elasticsearch)

```
┌─────────────┐   ┌──────────────┐   ┌──────────────┐
│   Gateway   │   │ OrderService │   │PaymentService│
└──────┬──────┘   └──────┬───────┘   └──────-┬──────┘
       │                 │                   │
       │  NLog (2 targets: file + Kafka)     │
       ├─────────────────┼───────────────────┤
       │                 │                   │
       ▼                 ▼                   ▼
  /var/log/app/*.log (daily rotation, 1 day kept)
       │                 │                   │
       └─────────────────┴───────────────────┘
                         │
                         ▼
                  ┌─────────────┐
                  │    Kafka    │
                  │ topic:      │
                  │  app-logs   │
                  └──────┬──────┘
                         │
                         ▼
                  ┌─────────────┐
                  │  Logstash   │
                  │  (consume   │
                  │   Kafka)    │
                  └──────┬──────┘
                         │
                         ▼
                  ┌─────────────┐
                  │Elasticsearch│
                  │   index:    │
                  │app-logs-*   │
                  └──────┬──────┘
                         │
                         ▼
                  ┌─────────────┐
                  │   Kibana    │
                  │ (Visualize) │
                  └─────────────┘
```

- Each service uses NLog with two targets: a daily-rotated file under `/var/log/app/*.log` (1 day kept) and Kafka topic `app-logs` on `kafka:9092`.
- Logstash consumes `app-logs` from Kafka (see `logstash/pipeline/logstash.conf`) and ships to Elasticsearch at `http://elasticsearch:9200`, indexing into `app-logs-YYYY.MM.dd`.
- Kibana connects to Elasticsearch to provide a UI for searching and visualizing logs.
- Tail a service log file: `docker compose exec orderservice sh -c "tail -n 50 /var/log/app/orderservice.log"`.
- Validate Kafka → Logstash → ES: `docker compose logs logstash --tail=200`, `curl http://localhost:9200/_cat/indices?v&pretty` and `curl http://localhost:9200/app-logs-*/_count?pretty`.
- Check kafka message `docker exec -it kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic app-logs --from-beginning`

How to use Kibana

1. **Access Kibana**: Open [http://localhost:5601](http://localhost:5601) in your browser.
2. **Create Data View** (First time only):
   - Go to **Stack Management** (gear icon) > **Data Views**.
   - Click **Create data view**.
   - **Name**: `App Logs`
   - **Index pattern**: `app-logs-*` (matches the indices created by Logstash).
   - **Timestamp field**: `@timestamp`.
   - Click **Save data view to Kibana**.
3. **View Logs**:
   - Go to **Discover** (compass icon).
   - Select your `App Logs` data view.
   - You can now search, filter, and view your application logs.

DB tracing and where to find SQL spans

- The `orderservice` uses a manual ActivitySource named `orderservice-db` to create DB spans around SQL operations. The activity name used by the code is `mysql.query`.
- The TracerProvider is configured to include that ActivitySource (so manual DB activities become spans) — look for `.AddSource("orderservice-db")` in `orderservice/Program.cs`.
- DB activities include standard tags such as `db.system` (`mysql`), `db.name`, `net.peer.name`, `db.user`, and `db.statement`. The code sets `db.statement` before executing the SQL, so you should see the `INSERT` SQL strings on the DB spans in Jaeger (note: Jaeger UI may truncate long statements).

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

Advanced: change exporter protocol

- The services send OTLP to Jaeger at `http://jaeger:4317` (gRPC). If you want to use OTLP HTTP instead, set the endpoint to `http://jaeger:4318/v1/traces` and ensure the exporter is configured for HTTP protobuf.

Run on Kubernetes

This project includes Kubernetes manifests in the `k8s/` directory to deploy the entire stack to a Kubernetes cluster.

### Prerequisites: Install Envoy Gateway

Before deploying the application, you must install the Envoy Gateway controller to handle the Gateway API resources:

```bash
# Install Envoy Gateway (latest version)
kubectl apply -f https://github.com/envoyproxy/gateway/releases/download/v1.0.0/install.yaml

# Verify the installation
kubectl get deployment -n envoy-gateway-system
kubectl get pods -n envoy-gateway-system
```

The gateway controller should be running in the `envoy-gateway-system` namespace before you proceed with application deployment.

1. **Build Images**:
   Since the images are built locally in the docker-compose setup, you need to build them and make them available to your cluster.

   ```bash
   docker build -t gateway:latest ./gateway
   docker build -t orderservice:latest ./orderservice
   docker build -t paymentservice:latest ./paymentservice
   ```

   *If using Minikube*:
   ```bash
   minikube image load gateway:latest
   minikube image load orderservice:latest
   minikube image load paymentservice:latest
   ```
   *If using Kind*:
   ```bash
   kind load docker-image gateway:latest
   kind load docker-image orderservice:latest
   kind load docker-image paymentservice:latest
   ```

2. **Deploy to Kubernetes**:
   Apply all manifests in the `k8s` folder:

   ```bash
   kubectl apply -f k8s/
   ```

3. **Access the Application**:
   *   **Minikube**: Run `minikube tunnel` in a separate terminal
   *   **Default NameSpace**:
         ```bash
         kubectl config set-context --current --namespace otel-demo
         ```

   **Gateway API hostname (gateway.otel-demo.local)**

   If you apply the Gateway API manifest [k8s/11-gateway.yaml](k8s/11-gateway.yaml), traffic is routed via an Envoy `Gateway` on port `80` using the hostname `gateway.otel-demo.local`. To use this hostname, add a hosts file entry pointing to the Gateway's external IP:

   1. Get the external IP of the Envoy Gateway:
      ```bash
      kubectl get svc -n otel-demo
      # Look for the Gateway service external IP (varies by environment)
      ```

   2. Add a hosts file entry for `gateway.otel-demo.local`:
      - Windows:
        - Open Notepad as Administrator
        - File → Open → `C:\Windows\System32\drivers\etc\hosts` (show All Files)
        - Add a line: `<EXTERNAL-IP> gateway.otel-demo.local`
        - Example (Docker Desktop / Minikube tunnel): `127.0.0.1 gateway.otel-demo.local`
      - macOS / Linux:
        ```bash
        # Replace <EXTERNAL-IP> with the value from kubectl
        echo "<EXTERNAL-IP> gateway.otel-demo.local" | sudo tee -a /etc/hosts
        ```

   **Other Services**:
   *   **Jaeger UI**: Port-forward to access the UI:
       ```bash
       kubectl port-forward svc/jaeger 16686:16686 -n otel-demo
       ```
       Visit `http://localhost:16686`.
   *   **Kibana**: Port-forward to access Kibana:
       ```bash
       kubectl port-forward svc/kibana 5601:5601 -n otel-demo
       ```
       Visit `http://localhost:5601`.
   *   **Kafka** Check the kafka messaging
       ```bash
       kubectl exec {pod-name} -n otel-demo -- /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic app-logs --from-beginning
      ```

4. **Cleanup**:
   To remove all resources:
   ```bash
   kubectl delete -f k8s/
   ```
