#!/usr/bin/env bash
# Generates the two Docker secret files required by the identity-hashing pipeline (FR-2.1):
#   - analytics_daily_salt_seed : secret seed for the daily-rotating Tier 1 salt
#   - analytics_durable_hmac_key: HMAC signing key for the Tier 2 durable hash
#
# These files are deliberately gitignored — see .gitignore. Run this once per environment before
# `docker compose up`. Both files are stored as lowercase hex so the DockerSecretReader can
# round-trip them to bytes; the trailing newline is trimmed on read.
set -euo pipefail

SECRETS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$SECRETS_DIR"

# 32 bytes (256 bits) of cryptographic randomness -> 64 hex chars. This is plenty for both a
# salt-derivation seed and an HMAC-SHA256 signing key.
openssl rand -hex 32 > "$SECRETS_DIR/analytics_daily_salt_seed"
openssl rand -hex 32 > "$SECRETS_DIR/analytics_durable_hmac_key"

chmod 600 "$SECRETS_DIR/analytics_daily_salt_seed" "$SECRETS_DIR/analytics_durable_hmac_key"

echo "Generated Docker secrets in $SECRETS_DIR:"
echo "  analytics_daily_salt_seed"
echo "  analytics_durable_hmac_key"
echo "These files are gitignored. Do NOT commit them. Rotate by re-running this script and restarting the stack."
