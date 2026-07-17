# Privacy-Engineered Analytics Dashboard
### Multi-Tenant, PII-Minimizing Web Analytics SaaS — .NET 10 + React 19
**Version 2.0 — Hardened Specification**



## EXECUTIVE SUMMARY

**Background** European enterprises face increasing regulatory pressure from GDPR and the Swedish Authority for Privacy Protection (IMY) regarding US-based analytics tools. Standard tracking solutions collect PII and cross-site tracking cookies, creating significant legal exposure.

**Business Objective** Build a production-representative, multi-tenant analytics SaaS platform using a decoupled .NET 10 backend and React 19 frontend. The platform delivers actionable web traffic insights while minimizing PII collection through a mathematically explicit Two-Tier Identity model, and integrates AI to convert natural-language questions into read-only, tenant-scoped SQL — enforced by the database, not by trusting the model.

**Success Criteria**
- **Privacy Engineering:** Demonstrable minimization of PII at the point of collection, with an honest, GDPR-literate account of what is and isn't anonymized — passing scrutiny in a technical/DPO-style review, not just a marketing claim.
- **Performance & Demonstrability:** Handle high-throughput event ingestion (10,000+ events/minute ≈ 167 events/sec sustained) without blocking the primary web application. One-click cold start via Docker Compose, paired with a K6 script that proves throughput and stability live.
- **Tenant Isolation:** Parameterized, defense-in-depth data isolation with explicit fail-closed guarantees on all read paths — including AI-generated queries, enforced through the same mechanism as every other read path, not a bespoke one.

---

## BUSINESS CONTEXT

**Current Workflow (The Problem)** Web administrators deploy client-side scripts that send raw IP addresses, User-Agents, and tracking cookies to centralized servers. Honoring a "Right to Erasure" request is nearly impossible in monolithic analytics databases, and unique-visitor metrics are often illegally correlated across sessions.

**Target Users**
1. **Marketing & Content Managers** — want "How many people viewed the pricing page yesterday?" without wading into a complex dashboard.
2. **Data Protection Officers (DPOs) / Admins** — need tracking that's defensible under GDPR/IMY and an efficient path to process erasure requests.

---

## FUNCTIONAL REQUIREMENTS

### Module 1: High-Throughput Event Ingestion & PII Minimization

**FR-1.1: Asynchronous Event Tracking**
- Actor: Website Visitor (via client script).
- Flow: Client script POSTs a JSON payload to `/api/v1/track` — URL, Referral Source, Event Type.
- The API strips all query-string parameters from the URL path before any processing — neutralizing the most common PII leak vector.
- **Residual Risk (explicitly accepted for MVP):** PII embedded directly in path segments (e.g. `/password-reset/user@email.com`) is not scrubbed in v1. A configurable, regex-based path-pattern scrubber is planned post-MVP. This is documented, not hidden — see Risk Register.
- The API generates identity hashes (Module 2) and publishes an `AnalyticsEventReceived` message to RabbitMQ, returning `202 Accepted` immediately.

**FR-1.2: Event Processing Worker**
- Actor: System (.NET 10 `BackgroundService`).
- Flow: Worker consumes RabbitMQ messages in batches and performs bulk inserts into PostgreSQL + TimescaleDB, chosen because it comfortably handles the ~167 events/sec target with far less operational overhead than a dedicated columnar/time-series platform (e.g. ClickHouse) — the right call at this scale, worth naming explicitly as a scoped trade-off, not an oversight.

---

### Module 2: Privacy, GDPR & The Two-Tier Identity Model

**FR-2.1: Two-Tier Identity — Pseudonymization, Not Anonymization**

> **Terminology matters here and the doc should say so out loud:** every identifier below is a *pseudonym*, per GDPR Recital 26 — data derived from identifying attributes that could theoretically be re-linked (e.g. via IP-space brute force against a known daily salt) is personal data, not anonymous data. The architecture's actual claim is **PII minimization and re-identification resistance**, not "zero PII." Being precise about this in front of a DPO persona is a stronger signal than an inflated claim that collapses under one follow-up question.

- **Tier 1 (Anonymous Traffic):** Daily ephemeral hash `SHA-256(IP + UA + DailySalt)`, stored as `AnonymousDailyHash`. The salt rotates daily, so the same real visitor produces an unlinkable hash from one day to the next — this is the actual privacy guarantee: no cross-day tracking of anonymous users, by construction.
- **Tier 2 (Authenticated Traffic):** If a user is logged into the client's platform and has opted in, a stable, tenant-scoped HMAC identifier (`DurableHash`) is attached to the tracking payload. The HMAC signing key is stored as a **Docker secret in local dev / a managed secrets store in any hosted deployment — never as a database column.** A key sitting next to the data it protects means one DB compromise re-identifies every authenticated user retroactively; keeping it out-of-band is a one-line architectural decision worth stating explicitly.
- **Deduplication & Metric Resolution:**
  - 7-day Unique Visitors for **Tier 2** are computed as exact counts (stable identifier → trivial `COUNT(DISTINCT)`).
  - 7-day Unique Visitors for **Tier 1** use HyperLogLog (HLL) aggregation, **documented explicitly as an upper-bound estimate, not an exact figure.** Because `AnonymousDailyHash` rotates daily, merging HLL sketches across days cannot recover a stable identity — the same real visitor contributes a "new" pseudonym each day it appears, so multi-day anonymous uniques trend toward overcounting as the window widens. This isn't a bug to fix; fixing it would require cross-day tracking of anonymous users, which is the exact thing the design refuses to do. The dashboard UI must visibly label this metric "Estimated" (see UI/UX Requirements) so the trade-off is visible to end users too, not just documented for interviewers.
  - `AnonymousDailyHash` is forced to `null` on ingestion whenever `IsAuthenticated = true`, preventing double-counting between tiers.

**FR-2.2: Right to Erasure (Data Purge)**
- Actor: DPO / Admin.
- Flow: Admin submits a known Tier 2 identifier. The system issues a hard delete for all associated records across the PostgreSQL + TimescaleDB store and logs the purge to an **immutable, append-only audit trail** (separate table, insert-only DB role — no update/delete grants — so the erasure log itself can't be quietly altered).
- Note: Tier 1 data requires no erasure path by design — it's already unlinkable after 24 hours, which is itself a strong DPO talking point ("anonymous data ages out of identifiability automatically").

---

### Module 3: AI-Powered Text-to-SQL Insights (Hardened Isolation Model)

**FR-3.1: Natural Language Querying & Server-Side Tenant Scoping**
- Actor: Marketing Manager.
- Flow: User types "Show me the top 5 pages by unique visitors this week."
- Backend injects a **restricted reporting-view schema** (not raw base tables) into a strict system prompt — the AI never sees table structures beyond what's safe to query.
- Backend calls the Deepseek V4 Flash API (via OpenRouter) with **Structured Outputs**, constraining the response to a single JSON field containing one SQL `SELECT` statement — the schema itself makes multi-statement or non-SELECT output a validation failure, not something a parser needs to catch after the fact.
- **Lightweight shape validation (defense-in-depth, not the primary control):** reject if the string contains a semicolon anywhere but a single optional trailing position, or contains any of `INSERT / UPDATE / DELETE / DROP / ALTER / GRANT / COPY / pg_`. This is a cheap belt-and-suspenders check — the real safety boundary is below.
- **Execution & Tenant Isolation — the actual control:** the query executes using a dedicated, least-privilege Postgres role with `SELECT`-only grants on the reporting views, inside a session where the backend has already run `SET LOCAL app.current_tenant_id = @tenantId`. **This is the identical Row-Level Security policy that gates the Dapper read path** (Module 4). There is no separate SQL-rewriting or custom grammar-parsing logic to write, test, or get subtly wrong — the AI path and the human-written-query path share one enforcement mechanism. If a prompt is engineered to omit or override tenant scope, the RLS policy fails closed regardless of what SQL the model produced.
- To de-risk live demos, the UI ships 3-4 pre-validated natural-language prompts. **These are cached responses, not just "known-good prompts that still hit the network live"** — see Demo Resilience Plan for why that distinction matters.

---

### Module 4: IAM & Defense-in-Depth Security

**FR-4.1: Multi-Tenant Authentication & Isolation**
- Actor: Platform Users.
- Flow: Users authenticate via Keycloak (local Docker). JWTs carry the user's `TenantId`.
- **Fail-Closed Isolation:** Read queries use Dapper. Tenant isolation is enforced by passing `@OrganizationId` into a parameterized Row-Level Security policy in PostgreSQL — **if `@OrganizationId` is missing, the policy returns zero rows**, not an error that could be swallowed.
- **Enforcement:**
  1. An automated test proves a Dapper query missing the tenant parameter returns zero rows (Phase 0 exit criterion).
  2. CI/CD fails any PR containing a Dapper query in the Analytics namespace that doesn't explicitly pass `@OrganizationId`.

---

## NON-FUNCTIONAL REQUIREMENTS

**NFR-1: Performance**
- Tracking API latency: < 50ms (p95).
- Dashboard reporting queries: < 1 second.
- **Caching:** Redis is deliberately not provisioned initially. Aggregation queries run directly against PostgreSQL + TimescaleDB. Redis is introduced *only if* measured p95 latency exceeds the 1-second SLA — this is a stated "not yet, and here's the trigger condition" decision, not an oversight.

**NFR-2: Architecture & Scalability**
- CQRS pattern via MediatR.
- Read queries use Dapper for raw execution speed; write commands use EF Core 10 for domain validation before queuing to RabbitMQ.

**NFR-3: Security, Compliance & Demonstrability**
- **Rate Limiting:** `/api/v1/track` is protected by .NET 10's built-in `RateLimiting` middleware, capped at **100 req/sec per tenant origin** (not a single global cap). This is consistent with the aggregate throughput target: multiple tenants can each sit near their own cap while the platform as a whole sustains 167+ events/sec. Stated explicitly so the numbers don't look contradictory under scrutiny.
- **Local IaC & Demo Portability:** PostgreSQL, TimescaleDB, RabbitMQ, Keycloak, the .NET 10 API, and the React frontend are fully containerized. A master `docker-compose.yml` provides one-click provisioning — see Demo Resilience Plan for the boot-ordering and health-check details that make this actually reliable in front of a live audience, not just on paper.
- **Storage:** No raw IP addresses or User-Agents are ever written to disk or to the RabbitMQ queue — only derived hashes.

---

## DATA MODELS (High-Level)

**Organization (Tenant)**
- `Id` (Guid, PK)
- `Name` (String)

**AnalyticsEvent** (PostgreSQL + TimescaleDB, hypertable partitioned on `Timestamp`)
- `EventId` (Guid, PK)
- `OrganizationId` (Guid — RLS partition key, enforced via policy parameter)
- `AnonymousDailyHash` (String, nullable — forced null if `IsAuthenticated = true`)
- `DurableHash` (String, nullable — tenant-scoped HMAC, authenticated users only)
- `IsAuthenticated` (Boolean)
- `EventType` (String: `Pageview`, `Click`)
- `Path` (String — scrubbed of query parameters; see FR-1.1 residual risk note)
- `Timestamp` (DateTimeOffset)

**ErasureAuditLog** (insert-only role, no update/delete grants)
- `AuditId` (Guid, PK)
- `OrganizationId` (Guid)
- `PurgedIdentifierHash` (String)
- `RequestedBy` (String)
- `PurgedAtUtc` (DateTimeOffset)
- `RecordsAffected` (Int)

---

## UI/UX REQUIREMENTS

**Design System:** React 19 + Tailwind CSS 4, Radix UI for accessible primitives, Recharts for data-dense visuals.

**Key Screens**
- **Real-Time Dashboard:** Time-series line chart. Data cards explicitly distinguish "Known Unique Visitors" (Tier 2, exact) from **"Estimated Anonymous Visitors" (Tier 1, HLL upper-bound)** — the "Estimated" label is a UI requirement, not a footnote, precisely because the underlying metric is a documented approximation (FR-2.1).
- **Ask AI Interface:** Chat-style panel docked to the side, returning dynamic data tables from tenant-scoped, RLS-enforced query results.

---

## KNOWN TRADE-OFFS & RISK REGISTER

Documenting these up front is the difference between "gap found live" and "deliberate decision, here's why."

| Risk / Trade-off | Deliberate? | Mitigation / Talking Point |
|---|---|---|
| Hashed IP/UA is pseudonymous, not anonymous, under GDPR | Yes — inherent to any hash-based approach | Framed correctly in the ADR; the real claim is minimization and re-identification resistance, not "zero PII." |
| 7-day anonymous unique visitors overcounts as window widens | Yes — required by the no-cross-day-tracking guarantee | Labeled "Estimated" in UI; documented as the direct, unavoidable cost of the privacy guarantee. |
| PII in URL path segments not scrubbed in MVP | Yes — scoped out, not missed | Explicit post-MVP item: regex-based path-pattern scrubber. |
| Five-service Docker Compose stack, Keycloak especially slow to cold-start | Partially — accepted for realism, mitigated operationally | Health-checked boot sequence; see Demo Resilience Plan. |
| Live external AI API call during a demo (network dependency) | Accepted for the "real" flow | Cached fallback responses for the 3-4 hardcoded prompts (see below). |
| PostgreSQL + TimescaleDB instead of a dedicated columnar/time-series engine | Yes — right-sized for the stated throughput | ~167 events/sec doesn't justify the operational cost of a separate analytics-only datastore; revisit if scale assumptions change. |
| No Redis cache in MVP | Yes — deferred until a measured SLA breach | Explicit trigger condition stated in NFR-1, not "we'll add caching eventually." |

---

## DEMO RESILIENCE PLAN

A live technical demo has different failure modes than production. This section exists because "it works on my machine" isn't good enough when someone is watching.

1. **Boot ordering:** `docker-compose.yml` uses `depends_on: condition: service_healthy` for every service, with real healthchecks (Postgres `pg_isready`, RabbitMQ management API ping, Keycloak `/health/ready`) — not fixed `sleep` delays.
2. **Keycloak specifically:** a pre-baked realm/client export is committed to the repo and imported automatically on first boot, so there's no live realm configuration step that can go wrong mid-demo.
3. **AI path fallback:** the 3-4 "hardcoded" natural-language prompts return **cached responses**, not just prompts that are merely likely to succeed against a live API. If OpenRouter/Deepseek has a bad moment or the venue's wifi doesn't cooperate, the demo doesn't stall — this is stated explicitly so "hardcoded" doesn't quietly mean "still one network hiccup from failure."
4. **Load test artifact:** a K6 script blasts `/api/v1/track` at 200 req/sec and renders a live throughput/latency chart, giving a visual, numeric answer to "does it actually scale" instead of an assertion.
5. **Rehearsal requirement:** the full cold-start sequence gets run end-to-end at least twice before any real demo — the first run always finds the thing that wasn't actually as automated as it looked on paper.

---

## PROJECT PHASES & MILESTONES

**Phase 0: Architecture Spikes, Security Validation & Legal Sign-off (Week 1)**
- **Exit Criteria 1 (Privacy Documentation):** Draft ADR for the Two-Tier Identity model, explicit about pseudonymization vs. anonymization and the HLL overcounting trade-off. Draft a lightweight **DPIA** (Data Protection Impact Assessment) — the artifact a Swedish DPO would actually expect for anything resembling large-scale monitoring, not just an internal ADR.
- **Exit Criteria 2 (Security):** ADR documents the RLS trust boundary as parameterized/backend-enforced. Automated test proves a Dapper query missing the tenant parameter fails closed.
- **Exit Criteria 3 (IAM):** Provision the least-privilege PostgreSQL role (SELECT-only, restricted to reporting views) for AI-generated query execution.
- **Exit Criteria 4 (Performance Load Test):** Prototype ingestion → RabbitMQ → PostgreSQL end-to-end. K6 script at 200 req/sec, visualizing queue consumption, validating <50ms p95.
- **Exit Criteria 5 (AI Tenant Isolation):** Confirm the AI execution path shares the Dapper path's RLS policy via session variable — no bespoke SQL-rewriting code exists. Test: submit a prompt engineered to omit/override tenant scope; confirm zero cross-tenant rows.
- **Exit Criteria 6 (Local Perimeter Validation):** Rate limiting validated under load, per-tenant-origin. `docker-compose.yml` finalized with healthchecks and dependency ordering — all services boot deterministically, including a pre-imported Keycloak realm.
- **Exit Criteria 7 (Infra dependency):** Custom Dockerfile extending the TimescaleDB image with the `hll` extension, built and verified in CI.

**Phase 1: Ingestion Pipeline MVP (Weeks 2-4)**
- .NET 10 Web API + EF Core write model.
- URL scrubbing middleware (query-string stripping; path-segment scrubbing explicitly deferred, see Risk Register).
- Background worker for PostgreSQL/TimescaleDB bulk insertion.

**Phase 2: Data Retrieval & UI (Weeks 5-7)**
- Dapper read queries, enforced by CI/CD linting + RLS.
- React 19 dashboard with Tailwind + Recharts, including the "Known vs. Estimated" visitor distinction.
- Keycloak authentication (local Docker), with realm export committed.

**Phase 3: AI Integration & Demonstration Polish (Weeks 8-9)**
- Deepseek V4 Flash integration for text-to-SQL via the RLS-session execution model.
- Multi-tenant isolation testing across both read paths.
- Cache the 3-4 demo prompt responses; finalize the Docker + K6 demonstration suite; run the full rehearsal pass (Demo Resilience Plan, item 5).

> **If time runs short:** cut AI-module ambition first (keep the cached demo prompts, drop any generalization work) — the ingestion pipeline, RLS isolation, and dashboard are what actually demonstrate backend engineering competence for a C# role. The AI module is a strong differentiator, not the load-bearing part of the story.

---

## APPENDIX: Anticipated Interview Questions

Prepared answers, not because the questions are traps, but because a well-rehearsed answer to a hard question is worth more than the doc itself.

**"You say zero PII — is that actually true under GDPR?"**
No, and the doc doesn't claim that. Hashed IP/UA is pseudonymous data under GDPR Recital 26 — it's derived from identifying attributes and re-identification is theoretically possible (e.g., brute-forcing a known daily salt against the IPv4 space). The actual engineering claim is PII *minimization* and re-identification resistance: raw IPs and UAs are never persisted, and Tier 1 hashes become unlinkable across days by construction.

**"Why does your 7-day anonymous unique count look like an overestimate?"**
Because it is, by design. The daily salt rotates specifically so anonymous users can't be tracked across days — that's the privacy guarantee. But it means the same real visitor produces a different hash each day, so merging HLL sketches across a 7-day window can't recover a stable identity and trends toward overcounting. Fixing that would mean adding cross-day tracking of anonymous users, which defeats the point. It's labeled "Estimated" in the UI for that reason.

**"How do you stop the AI from generating a query that leaks another tenant's data?"**
The AI path doesn't get special trust and doesn't get a bespoke isolation mechanism. It executes inside the same Row-Level Security policy that gates every Dapper read: the backend sets a session-scoped tenant variable before execution, and the RLS policy fails closed if that variable is missing or wrong — regardless of what SQL the model produced. There's no custom SQL parser trying to catch every unsafe pattern; the database enforces it structurally.

**"What if Keycloak fails to start during the demo?"**
The compose stack uses real healthchecks and `depends_on: condition: service_healthy`, plus a pre-baked realm export so there's no live configuration step. The full cold-start sequence is rehearsed at least twice before any live demo, specifically because that's where "should work" and "does work" tend to diverge.

**"Why Postgres + TimescaleDB instead of ClickHouse or a purpose-built analytics store?"**
At ~167 events/sec sustained, a dedicated columnar analytics engine is solving a scale problem this system doesn't have yet, at the cost of real operational complexity. TimescaleDB gets hypertable partitioning and reasonable aggregation performance without a second datastore to operate. That's a stated, revisitable decision, not a default.
