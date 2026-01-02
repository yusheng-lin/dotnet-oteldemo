# dotnet-oteldemo

Lightweight demo showing .NET services with OpenTelemetry -> Jaeger (OTLP) and a simple orders flow persisted to MySQL.

This repository contains three services wired with OpenTelemetry, an OpenTelemetry Collector, and Jaeger in `docker-compose.yml`:

- `gateway`  (ports: host 5000 -> container 8080)
- `orderservice` (ports: none exposed; called by gateway)
- `paymentservice` (ports: none exposed; called by orderservice)
- `otel-collector` (Receives OTLP data and forwards to Kafka)
- `otel-collector-consumer` (Consumes OTLP data from Kafka and forwards to Jaeger)
- `jaeger` (UI: http://localhost:16686)
- `mysql` (added for demo persistence, default port 3306)
- `kafka` (Message broker using KRaft mode - no ZooKeeper required)
- `kong` (API Gateway: http://localhost:8000, Admin: http://localhost:8001)
- `keycloak` (Identity Provider: http://localhost:8080)

Tracing Flow (Services → OTEL Collector → Kafka → OTEL Collector Consumer → Jaeger)

```
┌─────────────┐   ┌──────────────┐   ┌──────────────┐
│   Gateway   │   │ OrderService │   │PaymentService│
└──────┬──────┘   └──────┬───────┘   └──────┬───────┘
       │                 │                  │
       │          OTLP (gRPC/HTTP)          │
       └─────────────────┬──────────────────┘
                         │
                         ▼
                ┌──────────────────┐
                │  OTEL Collector  │
                │ (batch/process)  │
                └────────┬─────────┘
                         │
                         ▼
                ┌──────────────────┐
                │      Kafka       │
                │(topic:otlp_spans)│
                └────────┬─────────┘
                         │
                         ▼
                ┌──────────────────┐
                │  OTEL Collector  │
                │    (Consumer)    │
                └────────┬─────────┘
                         │
                         ▼
                ┌──────────────────┐
                │      Jaeger      │
                │   (Visualize)    │
                └──────────────────┘
```

- Each service is configured with `OTEL_EXPORTER_OTLP_ENDPOINT` pointing to `http://otel-collector:4317`.
- The `otel-collector` uses `otel-collector-config.yaml` to define its pipelines, receiving OTLP data and exporting it to Kafka (topic `otlp_spans`).
- The `otel-collector-consumer` uses `otel-collector-consumer-config.yaml` to consume spans from Kafka and export them to Jaeger.
- Jaeger receives data from the consumer collector and provides the UI for trace visualization.

What this README covers
- How to build and run the demo (docker-compose)
- How to generate traffic and verify traces in Jaeger
- How DB tracing is implemented and how to find DB spans
- Troubleshooting

Requirements
- Docker & Docker Compose
- .NET 8 SDK (for local builds if you want to `dotnet build`)

## Run with Docker Compose

From the repository root, build & start containers:

```bash
docker compose up -d --build
```

### Verification (Docker Compose)

1. **Generate traffic**:
```bash
# Direct access to gateway
curl -s http://localhost:5000/create-order

# Via Kong API Gateway (Authenticated)
TOKEN=$(curl -s -X POST "http://localhost:8080/realms/demo/protocol/openid-connect/token" \
  -d "grant_type=password" \
  -d "client_id=demo-app" \
  -d "username=testuser" \
  -d "password=testpass123" \
  -d "scope=openid" | jq -r '.access_token')

curl -H "Authorization: Bearer $TOKEN" http://localhost:8000/api/create-order
```

2. **Open Jaeger UI**: Visit [http://localhost:16686](http://localhost:16686) to visualize traces.

3. **Check Logs**:
```bash
# Tail a service log file
docker compose exec orderservice sh -c "tail -n 50 /var/log/app/orderservice.log"

# Check Kafka messages
docker exec -it kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic app-logs --from-beginning

# Verify Elasticsearch indices
curl http://localhost:9200/_cat/indices?v&pretty
curl http://localhost:9200/app-logs-*/_count?pretty
```

4. **Kibana**: Visit [http://localhost:5601](http://localhost:5601) (Follow the "How to use Kibana" section below for initial setup).

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

- The services send OTLP to the OpenTelemetry Collector at `http://otel-collector:4317` (gRPC). The collector then forwards this to Jaeger. If you want to change how services talk to the collector, you can update the `OTEL_EXPORTER_OTLP_ENDPOINT` and the collector's `receivers` configuration in `otel-collector-config.yaml`.

## Run on Kubernetes

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
   Build images and make them available to your cluster:

   ```bash
   docker build -t gateway:latest ./gateway
   docker build -t orderservice:latest ./orderservice
   docker build -t paymentservice:latest ./paymentservice
   ```

   *Minikube/Kind*: Load images into the cluster nodes:
   ```bash
   # Minikube
   minikube image load gateway:latest orderservice:latest paymentservice:latest
   # Kind
   kind load docker-image gateway:latest orderservice:latest paymentservice:latest
   ```

2. **Deploy to Kubernetes**:
   ```bash
   kubectl apply -f k8s/
   # Switch to the demo namespace
   kubectl config set-context --current --namespace otel-demo
   ```

3. **Configure Gateway Hostname**:
   Traffic is routed via an Envoy Gateway using the hostname `gateway.otel-demo.local`. Add a hosts file entry pointing to the Gateway's external IP:

   ```bash
   # Get the external IP (e.g., 127.0.0.1 if using minikube tunnel)
   kubectl get svc -n envoy-gateway-system
   # Add to /etc/hosts (Linux/macOS)
   echo "<EXTERNAL-IP> gateway.otel-demo.local" | sudo tee -a /etc/hosts
   ```

### Kafka Cluster on Kubernetes

The Kubernetes deployment runs a 3-broker Kafka cluster using a `StatefulSet` for high availability. This is a more robust setup compared to the single broker in Docker Compose.

- **Replicas**: The `StatefulSet` is configured with `replicas: 3`, creating three Kafka pods (`kafka-0`, `kafka-1`, `kafka-2`).
- **KRaft Mode**: The cluster runs in KRaft mode, without ZooKeeper. The controllers are co-located with the brokers.
- **Quorum**: The `controller.quorum.voters` is configured to include all three brokers, ensuring consensus.
- **Replication**: Internal topics are configured with `offsets.topic.replication.factor=3` and `transaction.state.log.replication.factor=3`. The minimum in-sync replicas (`transaction.state.log.min.isr`) is set to 2, which means a transaction can be acknowledged only if it has been written to at least two brokers. This setup can tolerate the loss of one broker.
- **Service Discovery**: A headless service (`kafka`) is used for DNS discovery of the brokers within the cluster. The advertised listeners are configured to use the pod's specific DNS name (e.g., `kafka-0.kafka.otel-demo.svc.cluster.local`).

### Verification (Kubernetes)

1. **Generate traffic**:
```bash
# Get an access token from Keycloak (Port-forward required, see below)
kubectl port-forward svc/keycloak 8080:8080 -n otel-demo > /dev/null 2>&1 &

TOKEN=$(curl -s -X POST "http://localhost:8080/realms/demo/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=password" \
  -d "client_id=demo-app" \
  -d "username=testuser" \
  -d "password=testpass123" \
  -d "scope=openid" | jq -r '.access_token')

# Access via Gateway hostname
curl -i -H "Authorization: Bearer $TOKEN" http://gateway.otel-demo.local/api/create-order
```

2. **Access UIs (Port-forwarding)**:
```bash
# Jaeger UI: http://localhost:16686
kubectl port-forward svc/jaeger 16686:16686 -n otel-demo > /dev/null 2>&1 &

# Elasticsearch: http://localhost:9200
kubectl port-forward svc/elasticsearch 9200:9200 -n otel-demo > /dev/null 2>&1 &

# Kibana: http://localhost:5601
kubectl port-forward svc/kibana 5601:5601 -n otel-demo > /dev/null 2>&1 &

# Kong Manager: http://localhost:8002
kubectl port-forward svc/kong 8002:8002 -n otel-demo > /dev/null 2>&1 &
```

3. **Check Logs/Messaging**:
```bash
# Check Kafka messages (e.g., from pod kafka-0)
kubectl exec -it kafka-0 -n otel-demo -- /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic app-logs --from-beginning
```

4. **Cleanup**:
```bash
kubectl delete -f k8s/
```

## Kong API Gateway & Keycloak Integration

The demo includes Kong API Gateway for routing and Keycloak for authentication.

### Complete System Architecture

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│                              CLIENT APPLICATION                                      │
└───────────────────────────────┬──────────────────────────────────────────────────────┘
                                │
                    ┌───────────┴──────────┐
                    │  1. Request Token    │
                    ▼                      │ 2. JWT Token
            ┌───────────────┐              │
            │   Keycloak    │◀─────────────┘
            │  Port: 8080   │
            │ (Auth Server) │
            └───────────────┘
                                │
                    ┌───────────┴──────────┐
                    │  3. API Request      │
                    │  (Bearer Token)      │
                    ▼                      │
            ┌───────────────┐              │
            │     Kong      │              │ 4. Validate Token
            │  Port: 8000   │──────────────┘    (OIDC Plugin)
            │ (API Gateway) │
            └───────┬───────┘
                    │ 5. Forward Request
                    ▼
┌──────────────────────────────────────────────────────────────────────────────────────┐
│                           APPLICATION SERVICES LAYER                                 │
│                                                                                      │
│   ┌─────────────┐         ┌──────────────┐         ┌───────────────┐                 │
│   │   Gateway   │────────▶│ OrderService │────────▶│PaymentService │                 │
│   │  Port: 8080 │  HTTP   │  Port: 8080  │  HTTP   │  Port: 8080   │                 │
│   └──────┬──────┘         └──────┬───────┘         └───────┬───────┘                 │
│          │                       │                         │                         │
│          │                       │ 6. DB Operations        │                         │
│          │                       └─────────┐               │                         │
│          │                                 ▼               │                         │
│          │                          ┌─────────────┐        │                         │
│          │                          │    MySQL    │        │                         │
│          │                          │  Port: 3306 │        │                         │
│          │                          │(ordersdb DB)│        │                         │
│          │                          └─────────────┘        │                         │
│          │                                                 │                         │
└──────────┼─────────────────────────────────────────────────┼─────────────────────────┘
           │                                                 │
           │ 7. OTLP Traces (gRPC/HTTP)                      │
           └─────────────────────┬───────────────────────────┘
                                 │
                                 ▼
┌──────────────────────────────────────────────────────────────────────────────────────┐
│                          OBSERVABILITY LAYER                                         │
│                                                                                      │
│  ┌──────────────────────────────────────────────────────────────────────────────┐    │
│  │                            TRACING PIPELINE                                  │    │
│  │                                                                              │    │
│  │    ┌──────────────────┐      ┌────────────────┐      ┌───────────────────┐   │    │
│  │    │  OTEL Collector  │─────▶│     Kafka      │─────▶│  OTEL Collector   │   │    │
│  │    │   (Producer)     │      │ Topic:         │      │    (Consumer)     │   │    │
│  │    │  Port: 4317/4318 │      │  otlp_spans    │      │                   │   │    │
│  │    └──────────────────┘      └────────────────┘      └─────────-┬────────┘   │    │
│  │                                                                 │            │    │
│  │                                                                 ▼            │    │
│  │                                                         ┌───────────────┐    │    │
│  │                                                         │    Jaeger     │    │    │
│  │                                                         │ Port: 16686   │    │    │
│  │                                                         │(Trace Visual) │    │    │
│  │                                                         └───────────────┘    │    │
│  └──────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                      │
│  ┌──────────────────────────────────────────────────────────────────────────────┐    │
│  │                            LOGGING PIPELINE                                  │    │
│  │                                                                              │    │
│  │    ┌──────────────────┐      ┌────────────────┐      ┌───────────────────┐   │    │
│  │    │   NLog (All      │─────▶│     Kafka      │─────▶│    Logstash       │   │    │
│  │    │    Services)     │      │ Topic:         │      │                   │   │    │
│  │    │ + File Rotation  │      │  app-logs      │      │                   │   │    │
│  │    └──────────────────┘      └────────────────┘      └─────────-┬────────┘   │    │
│  │                                                                 │            │    │
│  │                                                                 ▼            │    │
│  │                                                         ┌───────────────┐    │    │
│  │                                                         │Elasticsearch  │    │    │
│  │                                                         │ Port: 9200    │    │    │
│  │                                                         │Index:app-logs*│    │    │
│  │                                                         └───────┬───────┘    │    │
│  │                                                                 │            │    │
│  │                                                                 ▼            │    │
│  │                                                         ┌───────────────┐    │    │
│  │                                                         │    Kibana     │    │    │
│  │                                                         │  Port: 5601   │    │    │
│  │                                                         │ (Log Visual)  │    │    │
│  │                                                         └───────────────┘    │    │
│  └──────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                      │
└──────────────────────────────────────────────────────────────────────────────────────┘

Data Flow Summary:
1. Client requests JWT token from Keycloak
2. Keycloak returns JWT token
3. Client makes API request to Kong with Bearer token
4. Kong validates token with Keycloak (OIDC plugin)
5. Kong forwards authenticated request to Gateway service
6. Gateway → OrderService → PaymentService (HTTP calls)
7. OrderService persists data to MySQL
8. All services send OTLP traces to OTEL Collector (Producer)
9. OTEL Collector buffers traces in Kafka (otlp_spans topic)
10. OTEL Collector Consumer reads from Kafka and forwards to Jaeger
11. All services write logs via NLog (file + Kafka)
12. Logstash consumes logs from Kafka (app-logs topic)
13. Logstash indexes logs into Elasticsearch
14. Kibana provides UI for log visualization
15. Jaeger provides UI for trace visualization
```

### Default Credentials

| Service | Username | Password | URL |
|---------|----------|----------|-----|
| Keycloak Admin | `admin` | `admin` | http://localhost:8080 |
| Test User | `testuser` | `testpass123` | - |
| Admin User | `admin` (realm) | `adminpass123` | - |

### Useful URLs

| Service | URL | Description |
|---------|-----|-------------|
| Kong Proxy | http://localhost:8000 | API Gateway endpoint |
| Kong Admin | http://localhost:8001 | Kong Admin API |
| Kong Manager | http://localhost:8002 | Kong Admin GUI |
| Keycloak | http://localhost:8080 | Keycloak Admin Console |
| Keycloak OIDC Config | http://localhost:8080/realms/demo/.well-known/openid-configuration | OIDC Discovery |