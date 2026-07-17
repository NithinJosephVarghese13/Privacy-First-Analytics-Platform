#!/bin/bash
set -e

echo "Logging in to kcadm.sh..."
/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user admin --password admin

# We already created the realm and clients, but let's re-run or just fetch the IDs.
# If they exist, create will fail. So let's fetch IDs instead.
API_CLIENT_ID=$(/opt/keycloak/bin/kcadm.sh get clients -r analytics-platform -q clientId=analytics-api --fields id --format csv | tr -d '"')
WEB_CLIENT_ID=$(/opt/keycloak/bin/kcadm.sh get clients -r analytics-platform -q clientId=analytics-web --fields id --format csv | tr -d '"')

if [ -z "$API_CLIENT_ID" ]; then
  echo "Creating analytics-api client..."
  API_CLIENT_ID=$(/opt/keycloak/bin/kcadm.sh create clients -r analytics-platform -s clientId=analytics-api -s publicClient=false -s bearerOnly=true -i)
fi

if [ -z "$WEB_CLIENT_ID" ]; then
  echo "Creating analytics-web client..."
  WEB_CLIENT_ID=$(/opt/keycloak/bin/kcadm.sh create clients -r analytics-platform -s clientId=analytics-web -s publicClient=true -s 'redirectUris=["http://localhost:5173/*"]' -s 'webOrigins=["*"]' -s 'directAccessGrantsEnabled=true' -i)
fi

echo "Adding tenant_id claim mapper to analytics-web..."
cat <<EOF > /tmp/mapper.json
{
  "name": "tenant_id",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-usermodel-attribute-mapper",
  "config": {
    "claim.name": "tenant_id",
    "jsonType.label": "String",
    "user.attribute": "tenant_id",
    "id.token.claim": "true",
    "access.token.claim": "true"
  }
}
EOF
/opt/keycloak/bin/kcadm.sh create clients/$WEB_CLIENT_ID/protocol-mappers/models -r analytics-platform -f /tmp/mapper.json || echo "Mapper already exists"

echo "Adding tenant_id claim mapper to analytics-api..."
/opt/keycloak/bin/kcadm.sh create clients/$API_CLIENT_ID/protocol-mappers/models -r analytics-platform -f /tmp/mapper.json || echo "Mapper already exists"

echo "Creating user1 (tenant A)..."
/opt/keycloak/bin/kcadm.sh create users -r analytics-platform -s username=testuser1 -s enabled=true -s email=testuser1@tenant-a.com -s 'attributes={"tenant_id":["00000000-0000-0000-0000-000000000001"]}' || echo "User 1 already exists"
/opt/keycloak/bin/kcadm.sh set-password -r analytics-platform --username testuser1 --new-password password123

echo "Creating user2 (tenant B)..."
/opt/keycloak/bin/kcadm.sh create users -r analytics-platform -s username=testuser2 -s enabled=true -s email=testuser2@tenant-b.com -s 'attributes={"tenant_id":["00000000-0000-0000-0000-000000000002"]}' || echo "User 2 already exists"
/opt/keycloak/bin/kcadm.sh set-password -r analytics-platform --username testuser2 --new-password password123

echo "Done."
