# Lightweight Data Protection Impact Assessment (DPIA)

**System Name:** Privacy-Engineered Multi-Tenant Analytics Platform  
**Assessment Date:** 2026-07-23  
**Framework Alignment:** EU General Data Protection Regulation (GDPR Articles 25, 32, 35) & Swedish Authority for Privacy Protection (IMY) Enforcement Guidelines  
**Primary Reference:** [docs/spec.md](file:///c:/Programming/CSharp/Projects/privacy-first-analytics-dashboard/docs/spec.md) | [docs/adr-001-two-tier-identity-model.md](file:///c:/Programming/CSharp/Projects/privacy-first-analytics-dashboard/docs/adr-001-two-tier-identity-model.md)

---

## 1. System Overview & Processing Context

### 1.1 Processing Purpose
The platform provides multi-tenant web analytics as a service (SaaS) for enterprise organizations. The system records visitor interaction events (pageviews, clicks, referral sources) to generate real-time performance dashboards and natural-language analytical insights.

### 1.2 Data Controller & Processor Roles
* **Data Controller:** Enterprise Client subscribing to the analytics platform.
* **Data Processor:** Privacy-Engineered Analytics SaaS Platform.

### 1.3 Architectural Scope & Data Flows
```
[ Client Browser ]
        │
        ▼ (POST /api/v1/track)
[ API Gateway ] ───────► [ Query String Stripper Middleware ]
        │
        ▼ (In-Memory Transformation)
[ Identity Engine ] ────► Compute Pseudonym (Tier 1 Hash OR Tier 2 HMAC)
        │                 DROP RAW IP & USER-AGENT IMMEDIATELY
        ▼
[ RabbitMQ Queue ] ────► [ Worker Service ] ────► [ TimescaleDB Hypertable ]
```

---

## 2. Data Minimization & Identity Lifecycle

### 2.1 Processing at Ingestion Perimeter
1. **Query-String Stripping:** Upon receiving a payload at `/api/v1/track`, API middleware strips all query-string parameters (`?key=value`) from the inbound URL. This neutralizes common accidental PII leaks (e.g., authentication tokens or email addresses passed via URL params).
2. **Immediate PII Discard:** Inbound network metadata (raw IP address and User-Agent header) is extracted into volatile memory solely to compute identity pseudonyms. Raw IP addresses and User-Agents are **never** written to log files, RabbitMQ queues, or database tables.

### 2.2 Categorization of Identifiers

| Identifier Category | Formula / Origin | GDPR Classification | Persistence & Lifespan | Re-identification Risk |
|---|---|---|---|---|
| **Tier 1 (Anonymous)** | `SHA-256(IP + UA + DailySalt)` | Pseudonymous Personal Data (Recital 26) | Ephemeral (24-hour daily salt lifespan). Stored in `AnalyticsEvent.AnonymousDailyHash`. | **Low / Ephemeral**: Relies on brute-forcing known IP ranges within a 24h salt window. Unlinkable across daily boundaries. |
| **Tier 2 (Authenticated)** | `HMAC-SHA256(TenantID + UserID, Key)` | Pseudonymous Personal Data (Recital 26) | Durable (Until erased or subject to retention expiration). Stored in `AnalyticsEvent.DurableHash`. | **Medium**: Linked to opted-in authenticated account. Protected cryptographically by out-of-band HMAC signing key. |

---

## 3. Right to Erasure & Retention Mechanisms (GDPR Article 17)

### 3.1 Tier 1 Anonymous Data (Automated Aging)
* **Erasure Path:** No manual erasure endpoint is provided or required for Tier 1 data.
* **Legal & Technical Rationale:** Tier 1 pseudonyms auto-expire from identifiability every 24 hours when the daily salt rotates. Constructing an erasure mechanism for Tier 1 would require maintaining a stable cross-day index of anonymous visitors, which would violate the fundamental privacy-by-design requirement.

### 3.2 Tier 2 Authenticated Data (Hard Purge & Immutable Audit)
* **Trigger:** DPO / Tenant Admin submits a data subject erasure request referencing a known Tier 2 `DurableHash`.
* **Execution:**
  1. The API executes a **hard delete** across the PostgreSQL / TimescaleDB `AnalyticsEvent` hypertable for the target `DurableHash` within the scope of the requesting `OrganizationId`.
  2. In the **same atomic transaction**, an entry is inserted into the `ErasureAuditLog` table:
     - `AuditId`, `OrganizationId`, `PurgedIdentifierHash`, `RequestedBy`, `PurgedAtUtc`, `RecordsAffected`.
* **Database Role Security (Tamper Prevention):**
  - The application's database role holds **`INSERT` ONLY** permissions on `ErasureAuditLog`.
  - `UPDATE` and `DELETE` privileges are explicitly revoked for all application roles.
  - This structural constraint guarantees an immutable audit log that cannot be altered or purged by the application.

---

## 4. Comprehensive Risk Assessment & Mitigation Register

| Risk ID | Hazard / Risk Description | Severity (Initial) | Technical & Operational Controls | Residual Risk Level |
|---|---|---|---|---|
| **R-01** | **Re-identification of Tier 1 Ephemeral Hashes**<br>Attacker brute-forces IP space against known `DailySalt` to identify anonymous visitor traffic. | Medium | • Daily salt rotation (24h lifespan).<br>• High-entropy cryptographic salt generation.<br>• Data auto-ages into complete unlinkability. | **Low** (Accepted) |
| **R-02** | **Database Breach & Historical Re-identification**<br>DB dump allows retroactive computation of user pseudonyms. | High | • HMAC `SigningKey` stored strictly in **Docker Secrets / Managed Vault**.<br>• Signing key is **NEVER** stored in DB columns, `appsettings.json`, or env vars.<br>• Without the key, DB hashes cannot be reversed. | **Low** |
| **R-03** | **Cross-Tenant Data Leakage (Multi-Tenancy)**<br>Tenant A accesses or queries analytics events belonging to Tenant B. | High | • PostgreSQL **`FORCE ROW LEVEL SECURITY`** on `AnalyticsEvent`.<br>• All reads execute `SET LOCAL app.current_tenant_id` session variable via a shared Dapper helper.<br>• Missing/invalid tenant context returns 0 rows (fails closed). | **Very Low** |
| **R-04** | **PII Leakage in URL Path Segments**<br>Clients embed PII directly in paths (e.g., `/user/john@example.com`). | Medium | • Query strings stripped in MVP.<br>• Explicit post-MVP item: Configurable regex-based path-pattern scrubber.<br>• Client integration guidelines warn against path PII. | **Medium** (Documented MVP Risk) |
| **R-05** | **AI Text-to-SQL Prompt Injection / Exfiltration**<br>Malicious prompt attempts cross-tenant query or data modification (`DROP`/`UPDATE`). | High | • AI model constrained to **Structured Outputs** (single SELECT statement).<br>• Executes against **restricted reporting views**, not base tables.<br>• Executed under dedicated SELECT-only role using the **exact same RLS session variable** as normal reads. | **Low** |
| **R-06** | **Tier 1 Overcounting Misleading Business Metrics**<br>Multi-day HLL merges overcount anonymous unique visitors. | Low | • Frontend UI visibly labels Tier 1 unique visitor metrics as **"Estimated Anonymous Visitors"**.<br>• Documented as the direct cost of cross-day privacy guarantees. | **Negligible** |

---

## 5. DPO Compliance Statement & Approval Sign-off

### 5.1 Compliance Summary
The system design adheres to GDPR Article 25 (Data Protection by Design and by Default) and Article 32 (Security of Processing). Specifically:
1. **Data Minimization:** Raw PII (IP, UA, query strings) is stripped prior to storage.
2. **Storage Limitation:** Tier 1 identifiers lose linkability within 24 hours.
3. **Integrity & Confidentiality:** RLS and out-of-band secret management prevent cross-tenant leakage and key compromise risks.
4. **Accountability:** Immutable audit logging for right-to-erasure requests.

### 5.2 Sign-off Status
* **Assessment Outcome:** Approved for implementation subject to post-MVP path scrubber delivery.
* **Review Cycle:** Annual or upon major architecture changes to data ingestion.
