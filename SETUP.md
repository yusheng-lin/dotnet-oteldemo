# GitLab CI/CD Setup Guide for Minikube Deployment

This guide documents all manual steps required to set up the GitLab CI/CD pipeline for deploying to a local Minikube cluster.

## Prerequisites

- Docker and Docker Compose installed
- Minikube installed and running
- kubectl configured to access Minikube

## Table of Contents

1. [Start GitLab Services](#1-start-gitlab-services)
2. [Configure Docker for Insecure Registry](#2-configure-docker-for-insecure-registry)
3. [Get GitLab Initial Password](#3-get-gitlab-initial-password)
4. [Create GitLab Runner Token](#4-create-gitlab-runner-token)
5. [Register GitLab Runner](#5-register-gitlab-runner)
6. [Create Deploy Token for Container Registry](#6-create-deploy-token-for-container-registry)
7. [Create Kubernetes Secrets](#7-create-kubernetes-secrets)
8. [Configure Minikube to Access GitLab Registry](#8-configure-minikube-to-access-gitlab-registry)
9. [Fix Minikube Certificate Permissions](#9-fix-minikube-certificate-permissions)
10. [Run the Pipeline](#10-run-the-pipeline)

---

## 1. Start GitLab Services

```bash
docker compose -f docker-compose.gitlab.yml up -d
```

Wait for GitLab to be fully ready (can take 2-5 minutes):
```bash
docker compose -f docker-compose.gitlab.yml logs -f gitlab
# Wait until you see "gitlab Reconfigured!" or similar
```

## 2. Configure Docker for Insecure Registry

Since the GitLab registry uses HTTP (not HTTPS), configure Docker to allow it:

Edit `/etc/docker/daemon.json`:
```json
{
  "insecure-registries": ["gitlab.example.com:5050"]
}
```

Restart Docker:
```bash
sudo systemctl restart docker
```

## 3. Get GitLab Initial Password

```bash
docker compose -f docker-compose.gitlab.yml exec gitlab cat /etc/gitlab/initial_root_password
```

**Save this password!** Use it to login as `root` at http://gitlab.example.com

## 4. Create GitLab Runner Token

1. Open http://gitlab.example.com in your browser
2. Login as `root` with the initial password
3. Go to **Admin Area** (wrench icon) → **CI/CD** → **Runners**
4. Click **New instance runner**
5. Configure:
   - **Tags**: `minikube` (or leave empty)
   - **Run untagged jobs**: ✅ Check this
6. Click **Create runner**
7. **Copy the registration token** (starts with `glrt-...`)

## 5. Register GitLab Runner

Replace `<YOUR_RUNNER_TOKEN>` with the token from step 4:

```bash
docker compose -f docker-compose.gitlab.yml exec gitlab-runner gitlab-runner register \
  --non-interactive \
  --url http://gitlab.example.com \
  --token <YOUR_RUNNER_TOKEN> \
  --executor docker \
  --docker-image docker:27-cli \
  --docker-privileged=true \
  --docker-volumes "/var/run/docker.sock:/var/run/docker.sock" \
  --docker-volumes "/cache" \
  --docker-volumes "/home/$USER/.kube:/root/.kube:ro" \
  --docker-volumes "/home/$USER/.minikube:/home/$USER/.minikube:ro" \
  --docker-network-mode "host" \
  --docker-extra-hosts "gitlab.example.com:172.18.0.3" \
  --description "minikube-docker-runner"
```

> **Note**: The `--docker-network-mode "host"` is crucial for the runner to access both GitLab and Minikube.

### Verify Runner Registration

Check in GitLab UI: **Admin Area** → **CI/CD** → **Runners** - should show the runner as online.

Or via command:
```bash
docker compose -f docker-compose.gitlab.yml exec gitlab-runner gitlab-runner list
```

## 6. Create Deploy Token for Container Registry

1. Go to your project in GitLab: http://gitlab.example.com/yusheng/otel-demo
2. Navigate to **Settings** → **Repository** → **Deploy tokens**
3. Create a new token:
   - **Name**: `k8s-registry-access`
   - **Expiration date**: (set as needed or leave empty)
   - **Scopes**: ✅ `read_registry`
4. Click **Create deploy token**
5. **Copy both values immediately** (they won't be shown again):
   - **Username**: `gitlab+deploy-token-N` (e.g., `gitlab+deploy-token-1`)
   - **Token**: `gldt-xxxxxxxxxxxx`

## 7. Create Kubernetes Secrets

First, create the namespace:
```bash
kubectl create namespace otel-demo
```

Then create the registry secret (replace with your deploy token values):
```bash
kubectl create secret docker-registry gitlab-registry \
  --docker-server=gitlab.example.com:5050 \
  --docker-username=<DEPLOY_TOKEN_USERNAME> \
  --docker-password=<DEPLOY_TOKEN> \
  -n otel-demo
```

Example:
```bash
kubectl create secret docker-registry gitlab-registry \
  --docker-server=gitlab.example.com:5050 \
  --docker-username=gitlab+deploy-token-1 \
  --docker-password=gldt-sXeLZxToMXP7-zpiwehZ \
  -n otel-demo
```

Verify:
```bash
kubectl get secret gitlab-registry -n otel-demo
```

## 8. Configure Minikube to Access GitLab Registry

### 8.1 Start Minikube with Insecure Registry

Minikube must be started with the insecure registry flag to use HTTP instead of HTTPS:

```bash
# If Minikube is already running, delete and recreate it
minikube delete
minikube start --insecure-registry="gitlab.example.com:5050"
```

> **Important**: The `--insecure-registry` flag only works during initial creation. If you already have a Minikube cluster, you must delete and recreate it.

### 8.2 Add Hosts Entry

Minikube needs to know how to reach `gitlab.example.com`. Add a hosts entry inside Minikube:

```bash
minikube ssh "echo '$(minikube ssh "ip route | grep default | awk '{print \$3}'" 2>/dev/null | tr -d '\r') gitlab.example.com' | sudo tee -a /etc/hosts"
```

Verify:
```bash
minikube ssh "cat /etc/hosts | grep gitlab"
# Should show: 192.168.49.1 gitlab.example.com
```

> **Note**: This needs to be re-run if Minikube is restarted.

## 9. Fix Minikube Certificate Permissions

The GitLab runner needs to read Minikube certificates:

```bash
chmod -R a+r ~/.minikube/
```

> **Note**: This needs to be re-run every time Minikube is recreated with `minikube delete`.

## 10. Run the Pipeline

### Option A: Push to trigger pipeline
```bash
git add .
git commit -m "Your changes"
git push gitlab dev  # or main/master
```

### Option B: Manual trigger
1. Go to http://gitlab.example.com/yusheng/otel-demo/-/pipelines
2. Click **Run pipeline**
3. Select branch and click **Run pipeline**

### Option C: Run specific jobs manually
1. Go to the pipeline
2. Click on the job you want to run (e.g., `build-images`)
3. Click **Run** (play button)

---

## Troubleshooting

### Runner can't connect to Minikube
**Error**: `dial tcp 192.168.49.2:8443: i/o timeout`

**Solution**: Ensure runner is registered with `--docker-network-mode "host"`

### Permission denied reading Minikube certificates
**Error**: `unable to read client-key ... permission denied`

**Solution**: Run `chmod -R a+r ~/.minikube/`

### kubectl uses wrong cluster
**Error**: `The connection to the server localhost:8080 was refused`

**Solution**: Ensure `KUBECONFIG: /root/.kube/config` is set in the job's variables

### Docker API version mismatch
**Error**: `client version 1.43 is too old`

**Solution**: Use `docker:27-cli` or newer image in `.gitlab-ci.yml`

### ImagePullBackOff in Kubernetes
**Error**: Pods stuck in `ImagePullBackOff`

**Solutions**:
1. Verify the `gitlab-registry` secret exists: `kubectl get secret gitlab-registry -n otel-demo`
2. Verify Minikube can reach GitLab: `minikube ssh "curl -v http://gitlab.example.com:5050/v2/"`
3. Check the hosts entry: `minikube ssh "cat /etc/hosts | grep gitlab"`

### GitLab Registry not working
**Error**: Cannot push to registry

**Solution**: Ensure `docker-compose.gitlab.yml` has registry enabled on port 5050 and restart GitLab:
```bash
docker compose -f docker-compose.gitlab.yml down
docker compose -f docker-compose.gitlab.yml up -d
```

---

## Quick Reference

### GitLab URLs
- **GitLab Web UI**: http://gitlab.example.com
- **Container Registry**: http://gitlab.example.com:5050
- **SSH Clone**: `git clone ssh://git@gitlab.example.com:2222/yusheng/otel-demo.git`

### Important Files
- `docker-compose.gitlab.yml` - GitLab and Runner configuration
- `.gitlab-ci.yml` - CI/CD pipeline definition
- `k8s/10-apps.yaml` - Application deployments with registry config
- `register-gitlab-runner.txt` - Runner registration command reference

### Useful Commands

```bash
# Check GitLab status
docker compose -f docker-compose.gitlab.yml ps

# View GitLab logs
docker compose -f docker-compose.gitlab.yml logs -f gitlab

# List registered runners
docker compose -f docker-compose.gitlab.yml exec gitlab-runner gitlab-runner list

# Unregister all runners
docker compose -f docker-compose.gitlab.yml exec gitlab-runner gitlab-runner unregister --all-runners

# Check Kubernetes pods
kubectl get pods -n otel-demo

# Check pod events (for debugging)
kubectl describe pod <pod-name> -n otel-demo

# Restart deployments to pull new images
kubectl rollout restart deployment -n otel-demo
```
