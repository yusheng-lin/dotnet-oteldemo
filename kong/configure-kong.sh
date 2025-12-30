#!/bin/bash
# Kong configuration script - run after Kong is healthy
# This configures Kong to route requests to the gateway service with OIDC authentication

# set -e  # Exit on error

KONG_ADMIN_URL="${KONG_ADMIN_URL:-http://localhost:8001}"
GATEWAY_URL="${GATEWAY_URL:-http://gateway:8080}"
KEYCLOAK_EXTERNAL_URL="${KEYCLOAK_EXTERNAL_URL:-http://localhost:8080}"
KEYCLOAK_INTERNAL_URL="${KEYCLOAK_INTERNAL_URL:-$KEYCLOAK_EXTERNAL_URL}"

# Check if jq is available
if ! command -v jq &> /dev/null; then
    echo "WARNING: jq is not installed. Output will not be formatted."
    JQ_CMD="cat"
else
    JQ_CMD="jq ."
fi

echo "Waiting for Kong Admin API..."
until curl -s "$KONG_ADMIN_URL/status" > /dev/null 2>&1; do
    sleep 2
done
echo "Kong Admin API is ready!"

# Create the gateway service
echo "Creating gateway service..."
curl -s -X POST "$KONG_ADMIN_URL/services" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "gateway-service",
        "url": "'"$GATEWAY_URL"'"
    }' | $JQ_CMD

# Create route for the gateway service
echo "Creating gateway route..."
curl -s -X POST "$KONG_ADMIN_URL/services/gateway-service/routes" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "gateway-route",
        "paths": ["/api"],
        "strip_path": true
    }' | $JQ_CMD

echo ""
echo "============================================="
echo "Step 3: Fetching Keycloak public key..."
echo "============================================="

# Wait for Keycloak realm to be fully ready
echo "Waiting for Keycloak to be ready at ${KEYCLOAK_INTERNAL_URL}/realms/demo..."
until curl -s -f -o /dev/null "${KEYCLOAK_INTERNAL_URL}/realms/demo"; do
  echo "Keycloak not responding yet. Retrying in 5s..."
  sleep 5
done
echo "Keycloak is reachable."

# Get the realm's public key from Keycloak
REALM_INFO=$(curl -s "${KEYCLOAK_INTERNAL_URL}/realms/demo")
echo "Realm info retrieved"

# Extract the public key using jq
PUBLIC_KEY=$(echo "$REALM_INFO" | jq -r .public_key)
echo "Public key extracted (first 50 chars): $(echo "$PUBLIC_KEY" | head -c 50)..."

if [ -z "$PUBLIC_KEY" ] || [ "$PUBLIC_KEY" = "null" ]; then
  echo "ERROR: Could not extract public key from Keycloak!"
  echo "Realm Info dump:"
  echo "$REALM_INFO"
  exit 1
fi

echo ""
echo "============================================="
echo "Step 4: Creating JWT consumer..."
echo "============================================="
curl -i -X POST "$KONG_ADMIN_URL/consumers" \
  --data username=keycloak-jwt-consumer

echo ""
echo "============================================="
echo "Step 5: Adding JWT credentials for consumer..."
echo "============================================="

# Create a temporary file with the properly formatted RSA public key
cat > /tmp/rsa_key.pem << EOF
-----BEGIN PUBLIC KEY-----
$PUBLIC_KEY
-----END PUBLIC KEY-----
EOF

echo "RSA Key file contents:"
cat /tmp/rsa_key.pem

# Add JWT credential with the RSA public key
# Note: The key must match the 'iss' claim in the JWT token
# Keycloak issues tokens with iss matching the external URL used to access it
curl -i -X POST "$KONG_ADMIN_URL/consumers/keycloak-jwt-consumer/jwt" \
  -F algorithm=RS256 \
  -F "key=${KEYCLOAK_EXTERNAL_URL}/realms/demo" \
  -F "rsa_public_key=@/tmp/rsa_key.pem"

echo ""
echo "============================================="
echo "Step 6: Enabling JWT plugin on the service..."
echo "============================================="
curl -i -X POST "$KONG_ADMIN_URL/services/gateway-service/plugins" \
  --data name=jwt \
  --data config.claims_to_verify=exp

# Create health check service (no auth required)
echo ""
echo "Creating health service..."
curl -s -X POST "$KONG_ADMIN_URL/services" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "health-service",
        "url": "'"$GATEWAY_URL"'"
    }' | $JQ_CMD

echo "Creating health route..."
curl -s -X POST "$KONG_ADMIN_URL/services/health-service/routes" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "health-route",
        "paths": ["/health"],
        "strip_path": false
    }' | $JQ_CMD

echo ""
echo "Kong configuration complete!"
echo ""
echo "Available routes:"
curl -s "$KONG_ADMIN_URL/routes" | jq '.data[] | {name, paths}' || curl -s "$KONG_ADMIN_URL/routes"

# Cleanup
rm -f /tmp/rsa_key.pem