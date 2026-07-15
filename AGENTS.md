# AGENTS.md — Privacy-First Analytics Dashboard

## Project layout

- `backend/src/PrivacyAnalytics.Api` — ASP.NET Core API. Ingests `POST /api/v1/track`.
- `backend/src/PrivacyAnalytics.Worker` — background worker (queue consumer; stub for now).
- `backend/src/PrivacyAnalytics.Domain` — entities + identity-hashing interfaces and the
  `IdentityHashService` orchestrator (the FR-2.1 forced-null rule lives here, with no infra deps).
- `backend/src/PrivacyAnalytics.Contracts` — request DTOs.
- `backend/src/PrivacyAnalytics.Infrastructure` — EF Core `AnalyticsDbContext`, migrations, and
  the identity-hashing implementations (Docker secret reader, daily-salt, anonymous/durable hash
  providers) under `Identity/`.
- `backend/tests/PrivacyAnalytics.UnitTests` — pure unit tests (no DB, no Docker).
- `backend/tests/PrivacyAnalytics.IntegrationTests` — RLS tests requiring a live PostgreSQL.
- `docker/` — Dockerfiles, DB init, and `secrets/` (out-of-band identity secrets).

## Build & test

```bash
dotnet build PrivacyAnalytics.slnx          # from backend/
dotnet test tests/PrivacyAnalytics.UnitTests/PrivacyAnalytics.UnitTests.csproj
# Integration tests require the docker-compose stack (PostgreSQL/TimescaleDB):
#   docker compose up -d postgres-timescale
#   dotnet test tests/PrivacyAnalytics.IntegrationTests/PrivacyAnalytics.IntegrationTests.csproj
```

## Identity hashing (FR-2.1) — important conventions

- Two tiers, mutually exclusive per event:
  - Tier 1 (anonymous): `SHA-256(IP + UA + DailySalt)`, salt = `HMAC-SHA256(seed, date)`,
    rotates daily UTC. Stored as `AnonymousDailyHash`.
  - Tier 2 (authenticated + opted-in): tenant-scoped HMAC `HMAC-SHA256(key, orgId.ToString("N") + ":" + userId)`.
    Stored as `DurableHash`.
- **Forced-null rule:** `AnonymousDailyHash` is `null` whenever `IsAuthenticated` is true
  (enforced in `Domain.Identity.IdentityHashService`). Authenticated-but-not-opted-in stores NO
  identifier.
- **Secret management:** the HMAC signing key and the daily-salt seed are read from Docker secret
  files (`/run/secrets/analytics_durable_hmac_key`, `/run/secrets/analytics_daily_salt_seed`) via
  `DockerSecretReader`. They are NEVER a DB column, an appsettings literal, or an env var holding
  the value. `IdentityOptions` only holds paths/names.
  - Generate them: `./docker/secrets/generate-secrets.sh` (files are gitignored).
  - Local `dotnet run` works without them via `Identity:AllowUnmanagedDevSecrets: true` in
    `appsettings.Development.json` (dev fallback, loud warning). Production has it `false` →
    missing secret fails app startup fast.
- The pseudonyms are deliberately called *pseudonyms* (GDPR Recital 26), not anonymous data.
