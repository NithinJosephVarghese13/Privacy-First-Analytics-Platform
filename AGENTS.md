# AGENTS.md — Privacy-Engineered Analytics Dashboard

Multi-tenant, PII-minimizing web analytics SaaS. .NET 10 backend + React 19 frontend.
Every agent working in this workspace reads this file before starting a task. It encodes
hard constraints from the spec as rules, not suggestions — treat violations as build-breaking,
not style nits.

## Source of Truth

- `/docs/spec.md` — the hardened spec, committed
  to the repo. This file states *what's non-negotiable*; the spec explains *why*. When a task
  references an FR/NFR number, read that section in `/docs/spec.md` before writing anything —
  don't reconstruct it from memory of an earlier session in this workspace.
- The project is built in 11 scoped sessions (see the team's build runbook). Each task has a
  concrete, checkable milestone. If a task you're given doesn't have one, ask for it before
  starting — an unverifiable task is a sign it's scoped too large.

## Toolchain — Pinned, Do Not Drift

- **.NET SDK 10.0.301** exactly, target framework `net10.0`. Don't downgrade syntax or
  reach for pre-.NET-10 idioms because they're more familiar from training data.
- **React 19.2.7**, TypeScript, Vite, Tailwind CSS, Radix UI, Recharts.
- **Docker Desktop 4.82.0** — Compose V2 syntax, `depends_on: condition: service_healthy`
  is required, not optional (see Compose rules below).
- If a pinned version conflicts with something a task needs, stop and flag it. Don't silently
  substitute a different version to make something compile.

## Repository Structure — Fixed Boundaries

```
/backend
  /src
    PrivacyAnalytics.Api             ASP.NET Core 10 Web API — thin, no business logic
    PrivacyAnalytics.Worker          .NET 10 BackgroundService — RabbitMQ consumer
    PrivacyAnalytics.Domain          class library, zero external dependencies
    PrivacyAnalytics.Infrastructure  EF Core 10, Dapper, RabbitMQ client — all data access lives here
    PrivacyAnalytics.Contracts       shared DTOs, referenced by Api and Worker
  /tests
  PrivacyAnalytics.slnx
/docker
  /init-db
  /secrets                          HMAC signing key and other Docker secrets live here — never in appsettings, never in a DB column
  Dockerfile.api
  Dockerfile.postgres-timescale     custom Timescale image, includes the hll extension
  Dockerfile.rabbitmq
  init-hll.sh
/frontend                           React 19 + TypeScript + Vite + Tailwind, independent app
/docs/spec.md                       hardened spec — single source of truth
docker-compose.yml
```

This is the real, current layout — verify it stays this way rather than reverting to a
different shape mid-project. Elsewhere in this file, `Api`/`Worker`/`Domain`/`Infrastructure`/
`Contracts` refer to `PrivacyAnalytics.Api`/`.Worker`/`.Domain`/`.Infrastructure`/`.Contracts`
under `/backend/src`.

Reference direction is fixed: `Api` and `Worker` depend on `Infrastructure` and `Domain`;
`Infrastructure` depends on `Domain`. **Nothing depends on `Api` or `Worker`.** Do not put
business logic in `Api` "to keep it simple" — this boundary is what makes the CQRS split
below enforceable instead of aspirational. This is a naming/nesting convention only — do not
rename or re-nest these projects to match an earlier draft of this file; the layout above is
authoritative.

## Hard Architectural Rules — Non-Negotiable

### CQRS split
MediatR for command/query dispatch. **Reads → Dapper only. Writes → EF Core 10 only.**
Never cross this line in the `Analytics` namespace, including for "just one quick read."
If a task seems to need an EF Core read or a Dapper write, stop and flag it instead of doing it.

### Row-Level Security — fail closed, always
- `analytics_event` has RLS enabled with **`FORCE ROW LEVEL SECURITY`** — plain `ENABLE`
  silently exempts the table-owner role and makes the policy a no-op for the app's own
  connection. This is the single easiest way to make this system look secure while it isn't.
- Policy predicate: `organization_id = current_setting('app.current_tenant_id', true)::uuid`.
- **Every** Dapper query executes through one shared helper that runs
  `SET LOCAL app.current_tenant_id = @tenantId` (sourced from `ICurrentTenant`) before the
  real query, in the same session/transaction. No handler opens its own connection and skips
  this — ever.
- CI fails any PR containing a Dapper call in the `Analytics` namespace that bypasses the
  helper (Roslyn analyzer or a CI script — either is fine, absence of the check is not).
- Missing or invalid tenant context returns **zero rows**, never an exception. Treat any
  change near this behavior as high-risk: plan first, don't just patch it.

### Two-Tier Identity
- Tier 1 (anonymous): `AnonymousDailyHash = SHA-256(IP + UserAgent + DailySalt)`. The salt
  rotates daily — that's what makes Tier 1 unlinkable across days. Don't stabilize the salt to
  "fix" the resulting 7-day HLL overcount; the overcount is the privacy guarantee working as
  designed, not a bug.
- Tier 2 (authenticated + opted-in): `DurableHash` = tenant-scoped HMAC.
- `AnonymousDailyHash` is forced `null` whenever `IsAuthenticated = true`.
- HMAC signing key: Docker secret file, mounted at runtime. **Never** in `appsettings.json`,
  **never** as an environment variable holding the literal key, **never** as a database column.
  Grep for it before calling any identity-related task done.
- Raw IP addresses and User-Agents are never persisted — not to Postgres, not to the RabbitMQ
  queue. Only derived hashes cross that boundary.
- Unique-visitor metrics: Tier 2 is exact (`COUNT(DISTINCT DurableHash)`). Tier 1 is an HLL
  **estimate** — return it as a separate, explicitly labeled field, never merged with Tier 2.
  The frontend must never render the Tier 1 number without an "Estimated" label — this is a
  UI requirement, not a nice-to-have.

### Right to Erasure
- Hard delete by Tier 2 identifier + one insert into `ErasureAuditLog`, in the same
  transaction — a purge that isn't logged must be structurally impossible.
- `ErasureAuditLog` is insert-only: the app's DB role has no `UPDATE`/`DELETE` grant on it.
- Tier 1 gets no erasure path. Don't build one — it's unlinkable after 24h by design, and an
  erasure mechanism would imply a stable identifier that shouldn't exist in the first place.

### AI text-to-SQL (Module 3) — highest blast radius, go slow
- The model sees only restricted reporting **views** in its schema context, never base tables.
- OpenRouter → Deepseek V4 Flash, Structured Outputs constrained to exactly one SQL `SELECT`
  string.
- Shape validation (reject stray semicolons past one optional trailing position; reject
  `INSERT/UPDATE/DELETE/DROP/ALTER/GRANT/COPY/pg_`) is defense-in-depth **only**. Say so in a
  code comment at the point it's implemented — it must never be documented or treated as the
  real security boundary.
- Execution uses the **exact same** Dapper RLS helper as every other read: same
  `SET LOCAL app.current_tenant_id`, same least-privilege `SELECT`-only DB role restricted to
  the reporting views. **Do not write a separate SQL-rewriting, parsing, or validation layer
  for tenant scoping.** If a task seems to call for one, that's a signal to stop, not proceed.
- Any change here needs an adversarial isolation test: a prompt engineered to omit or override
  tenant scope must still return zero cross-tenant rows. Prove the test isn't a false positive
  by temporarily widening the AI role's grants and confirming the test then fails.

### Rate limiting & performance
- .NET 10's built-in `RateLimiting` middleware on `/api/v1/track`, partitioned **per tenant
  origin**, 100 req/sec per tenant — not one global cap. (Platform-wide target is ~167
  events/sec sustained; the per-tenant cap and the aggregate target are consistent, not
  contradictory — say so if asked.)
- Targets: tracking API p95 < 50ms; dashboard reporting queries < 1s.
- No Redis. Don't add a cache speculatively — the spec's trigger is a *measured* p95 breach
  of the 1s dashboard SLA, not "this might get slow eventually."

### Auth
- Keycloak (local Docker), `tenant_id` in the JWT claims.
- JWT is parsed once into a scoped `ICurrentTenant`. Handlers never parse a token themselves.
- Realm/client config is exported and committed; it must auto-import on `docker compose up`
  from a clean volume. No manual admin-console step is acceptable, even in dev.

### Docker Compose
- Every service gets a real healthcheck (`pg_isready` for Postgres, the RabbitMQ management
  API for RabbitMQ, `/health/ready` for Keycloak) and `depends_on: condition: service_healthy`.
- No `sleep`-based ordering hacks, anywhere, ever.

### Demo mode
- The 3-4 curated AI prompts return pre-cached responses with **zero live network call** —
  not just prompts that are merely likely to succeed. The live/cached toggle must be visibly
  displayed in the UI, never a silent fallback.

## Session & Task Discipline

This project is deliberately built session-by-session. These rules exist because long agent
sessions drift — constraints stated early get diluted by turn 20, and "finished" work left
sitting in an open chat invites unrequested changes.

- **One task = one verifiable slice.** If a task doesn't have a concrete check (a command
  that passes, a curl that returns the right thing, a test that's green), it's too big — say
  so and propose a split before starting.
- **Plan before code, for anything non-trivial.** Give a short plan first: approach, files
  touched, anything you're unsure about. Wait for approval before writing code.
- **Restate the constraints relevant to this specific task at the start of your plan.** Don't
  assume something said earlier in a long session still holds — say again "this read goes
  through the RLS helper," "writes only via EF Core," etc., every time it applies.
- **When a milestone fails, explain why before changing anything.** Root-cause first. Don't
  default to rewriting — a rewrite that papers over the actual cause resurfaces later, usually
  at a worse time.
- **Stop at the milestone.** Once a task's milestone passes, say so clearly and stop. Don't
  keep making unrelated changes in the same task just because the session is still open.
- **Stay inside the task's declared file scope.** If finishing correctly requires touching a
  file outside that scope, say so and ask rather than expanding scope silently.
- **Pull requirements from `/docs/spec.md`, not from memory of an earlier conversation** —
  a paraphrase from three sessions back is not authoritative.

## Definition of Done

- Build succeeds with zero warnings on a clean checkout.
- The task's specific milestone passes. For anything touching the async pipeline (ingestion,
  worker, RabbitMQ), run it more than once — these are exactly the tests that pass once and
  fail on retry.
- No secret material (HMAC key, Keycloak client secret, OpenRouter API key) appears in
  `appsettings.json`, environment variable dumps, source, or the database.
- Commit message references the task number, e.g.
  `feat: track endpoint with URL scrubbing + rate limiting [Task 3.1]`.
- Nothing outside the task's declared scope changed.

## Explicit Anti-Patterns

These are flagged drift patterns, not hypothetical concerns:

- Collapsing everything into `Api` "to keep it simple" — breaks the CQRS boundary the whole
  project depends on.
- A Dapper read that skips the RLS session-variable helper, for any reason.
- A second SQL-rewriting or grammar-parsing layer for the AI query path — it must ride the
  existing RLS mechanism, not get its own.
- `ENABLE ROW LEVEL SECURITY` without `FORCE`.
- Redis, or any cache, added before a measured SLA breach.
- `sleep`-based service ordering in `docker-compose.yml`.
- Describing the AI shape-validation regex as a real security boundary, in code comments,
  docs, or conversation.
- A stable (non-rotating) anonymous salt to "fix" the HLL overcount.

## Antigravity Configuration Notes

This file is the always-loaded, project-wide layer. A few things are worth setting up
alongside it rather than letting this file grow indefinitely:

- **`.agents/rules/`** — this project has enough distinct rule domains (RLS enforcement,
  identity/privacy model, AI isolation, demo resilience) to justify splitting them into
  separate workspace rule files instead of one growing document. Keep AGENTS.md as the terse,
  load-bearing version; put longer rationale and edge cases in the split files.
- **`GUARDRAILS.md`** — worth adding specifically ahead of the RLS work (Sessions 2, 6) and
  the AI integration (Session 9). Those are the tasks most likely to produce an agent stuck
  patching a subtly wrong fix in a loop; `GUARDRAILS.md` backs up the project's own rule of
  resetting to the last good commit and starting a tighter prompt, rather than letting a
  confused session keep going.
- **Autonomy profile: Review-driven development, not Agent-driven,** for this project.
  Enough of its correctness lives in things that fail silently when wrong — an RLS predicate,
  an insert-only grant, where a key is sourced from — that I'd keep human checkpoints on for
  Sessions 2, 6, 8, and 9 specifically, even once you're comfortable with the tool elsewhere.
- **`skills/`** — worth codifying once you've done the RLS-wired-Dapper-query pattern by hand
  once (Task 6.1/6.2). A skill that encodes "new reporting query → goes through the shared
  helper → gets an isolation test" saves you re-explaining the pattern in both Session 6 and
  Session 9.
- **Known limitation to plan around:** agent context does not persist between sessions the
  way it might in a tool with longer-running memory — AGENTS.md is re-read at the start of
  each session, but a correction given mid-session (e.g. "use `SET LOCAL`, not `SET`") doesn't
  carry forward on its own. That's exactly why "restate constraints" and "plan before code"
  are rules above, not stylistic suggestions — they compensate for that reset.
