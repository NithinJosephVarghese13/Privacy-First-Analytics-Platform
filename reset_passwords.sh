#!/bin/bash
set -e
/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user admin --password admin

USER1_ID=$(/opt/keycloak/bin/kcadm.sh get users -r analytics-platform -q username=testuser1 --fields id --format csv | tr -d '"')
/opt/keycloak/bin/kcadm.sh update users/$USER1_ID/reset-password -r analytics-platform -s type=password -s value=password123 -s temporary=false

USER2_ID=$(/opt/keycloak/bin/kcadm.sh get users -r analytics-platform -q username=testuser2 --fields id --format csv | tr -d '"')
/opt/keycloak/bin/kcadm.sh update users/$USER2_ID/reset-password -r analytics-platform -s type=password -s value=password123 -s temporary=false
