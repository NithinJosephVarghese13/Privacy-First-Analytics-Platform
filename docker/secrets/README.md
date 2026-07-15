# Docker secrets for identity hashing (FR-2.1)

This directory holds the **out-of-band** secret files consumed by the two-tier identity pipeline.
The durable-HMAC signing key (Tier 2) and the daily-salt seed (Tier 1) are read from here at
runtime by `DockerSecretReader`. They are **never** stored in a database column, in `appsettings`,
or in an environment variable holding the literal key value.

## Why out-of-band?

A signing key sitting next to the data it protects means a single database compromise re-identifies
every authenticated user retroactively. Keeping the key in a Docker secret (or a managed secrets
store in hosted deployments) is a one-line architectural decision that materially raises the bar for
re-identification.

## Generate the secrets

```bash
./docker/secrets/generate-secrets.sh
```

This creates two 256-bit hex files:

- `analytics_daily_salt_seed` — seed for `HMAC-SHA256(seed, date)` → the daily-rotating Tier 1 salt.
- `analytics_durable_hmac_key` — the Tier 2 HMAC-SHA256 signing key.

Both files are **gitignored** (see `.gitignore`). Do not commit them. Rotate by re-running the
script and restarting the stack.

## Local `dotnet run` (outside Docker)

Set `Identity:AllowUnmanagedDevSecrets: true` (already set in `appsettings.Development.json`). When a
secret file is missing the app falls back to a fixed, clearly-warned dev value so local development
works without mounting anything. This fallback is **disabled** in production
(`AllowUnmanagedDevSecrets: false`) — a missing secret then fails app startup fast rather than
silently degrading re-identification resistance.
