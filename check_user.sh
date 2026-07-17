#!/bin/bash
set -e
/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user admin --password admin
USER1_ID=$(/opt/keycloak/bin/kcadm.sh get users -r analytics-platform -q username=testuser1 --fields id --format csv | tr -d '"')
/opt/keycloak/bin/kcadm.sh get users/$USER1_ID -r analytics-platform
