# ADR 001: Two-Tier Identity Model for PII Minimization

* **Status:** Accepted
* **Deciders:** System Architect, Data Protection Officer (DPO), Lead Backend Engineer
* **Date:** 2026-07-23
* **Context Document:** [docs/spec.md](file:///c:/Programming/CSharp/Projects/privacy-first-analytics-dashboard/docs/spec.md)

---

## 1. Context & Problem Statement

European enterprise web applications operating under GDPR enforcement—and specific guidance from regulatory authorities such as the Swedish Authority for Privacy Protection (IMY)—face compliance risks when deploying standard client-side analytics tools. Traditional analytics platforms transmit and store raw IP addresses, User-Agent strings, and cross-site tracking cookies. These identifiers constitute Personal Identifiable Information (PII) under GDPR Article 4(1).

Standard tracking methodologies introduce significant regulatory exposure:
1. Cross-site tracking and persistent user profiling without explicit consent violate ePrivacy and GDPR consent directives.
2. Inability to execute true data minimization at the point of ingestion leads to bloated PII storage.
3. Database breaches expose historical visitor identities retroactively.

The platform requires a high-throughput event ingestion mechanism (~167 events/sec sustained) that delivers accurate pageviews and unique-visitor metrics while mathematically guaranteeing PII minimization and re-identification resistance.

---

## 2. Decision Drivers

* **GDPR Recital 26 Compliance:** Precise legal framing of pseudonymization vs. anonymization to withstand technical DPO scrutiny.
* **Zero Persistence of Raw PII:** Neither raw IP addresses nor User-Agent strings may ever be written to disk, database tables, or message queues.
* **Unlinkability of Anonymous Traffic Across Days:** Anonymous traffic must be structurally impossible to correlate across daily boundaries.
* **Support for Authenticated Analytics:** Allow opted-in, authenticated users to be tracked accurately across sessions without compromising key security.
* **Cryptographic Key Isolation:** Protect pseudo-anonymous authenticated identifiers against retroactive database compromise.
* **Honest Metrics Representation in UI/UX:** Explicit distinction between exact metrics and statistical estimates.

---

## 3. Architecture Decisions

### 3.1 Pseudonymization vs. Anonymization Framing (GDPR Recital 26)

> **Key Architectural Claim:** The platform provides **PII minimization and re-identification resistance**, NOT "zero PII" or absolute anonymization.

Under GDPR Recital 26, data derived from natural persons remains *pseudonymous* if it can be re-linked to an individual using additional information, regardless of where that additional information is stored. Because IP address ranges are finite and known, a simple SHA-256 hash of an IP address and User-Agent—even with a daily salt—remains theoretically susceptible to brute-force dictionary attacks across the IP v4/v6 address space within that 24-hour salt window.

Therefore:
- All generated visitor hashes (`AnonymousDailyHash` and `DurableHash`) are formally classified as **pseudonymous data**.
- The architectural claim presented to auditors and DPOs is **strict data minimization and high re-identification resistance**.
- Raw network metadata (IP address and User-Agent header) is extracted in-memory at the `/api/v1/track` endpoint, processed into derived hashes, and **immediately discarded from memory**.

### 3.2 Tier 1: Ephemeral Anonymous Visitor Model

For unauthenticated website visitors (or users who have not opted into tracking):

* **Identifier Formula:** 
  $$\text{AnonymousDailyHash} = \text{SHA-256}(\text{IP} + \text{UserAgent} + \text{DailySalt})$$
* **Salt Lifetime & Rotation:** `DailySalt` is a high-entropy secret that rotates automatically every 24 hours at 00:00 UTC.
* **Cross-Day Unlinkability:** Because the salt rotates daily, the same physical device/visitor yields entirely distinct, uncorrelated hashes on consecutive days. Cross-day identity correlation is prevented by construction.
* **HyperLogLog (HLL) Aggregation & Overcounting Trade-off:**
  - Multi-day unique visitor counts (e.g. 7-day or 30-day uniques) for Tier 1 traffic use HyperLogLog (HLL) cardinality sketches (`hll` PostgreSQL extension).
  - **The Trade-off:** Merging daily HLL sketches across a multi-day window causes the unique visitor metric to trend toward **overcounting** as the window widens (a visitor appearing on 3 separate days contributes 3 separate daily pseudonyms to the multi-day sketch).
  - **Rationale for Acceptance:** Stabilizing the salt across days to "fix" the HLL overcount would enable cross-day tracking of anonymous users—the exact vector this design explicitly refuses to allow. Overcounting is the unavoidable, deliberate cost of the privacy guarantee working as designed.
  - **UI Obligation:** The frontend dashboard MUST render Tier 1 unique metrics with an explicit "Estimated" label.

### 3.3 Tier 2: Authenticated & Opted-In Visitor Model

For users logged into a client platform who have explicitly opted in:

* **Identifier Formula:**
  $$\text{DurableHash} = \text{HMAC-SHA256}(\text{TenantID} + \text{UserID}, \text{SigningKey})$$
* **Key Management & Storage:**
  - The `SigningKey` is loaded at runtime via **Docker Secrets** (local/dev) or a dedicated secret store (e.g. HashiCorp Vault / AWS Secrets Manager in production).
  - **Strict Prohibition:** `SigningKey` is **NEVER** stored in `appsettings.json`, environment variables containing literal secrets, or database tables.
  - **Security Rationale:** Storing the signing key in a database column or configuration file would mean a database leak allows an attacker to retroactively compute `DurableHash` for known UserIDs. Keeping the key out-of-band ensures database compromise cannot break identity isolation.
* **Tier Mutual Exclusivity:**
  - On ingestion, if `IsAuthenticated = true` and a valid `DurableHash` is provided, `AnonymousDailyHash` is explicitly forced to `null`.
  - This prevents double-counting of visitors across Tier 1 and Tier 2 datasets.

### 3.4 PII Scrubbing & Residual Risk Acceptance

* **Query String Stripping:** All query-string parameters (e.g., `?email=user@test.com&token=123`) are stripped from inbound URLs in API middleware prior to processing.
* **Path Segment Residual Risk (MVP Scope):**
  - *Residual Risk:* URLs containing PII directly inside path segments (e.g., `/reset-password/user@example.com`) are not automatically scrubbed in v1.0.
  - *Mitigation Plan:* Documented as an explicit post-MVP item: a configurable, regex-based path-pattern scrubber.

---

## 4. Summary of Trade-offs & Risk Register

| Risk / Architectural Trade-off | Status | Mitigation & Design Justification |
|---|---|---|
| Hashed IP/UA is pseudonymous, not anonymous | Deliberate | Framed accurately under GDPR Recital 26. Focus is on PII minimization and re-identification resistance. |
| 7-day Tier 1 unique visitors overcounts as window widens | Deliberate | Required by the no-cross-day-tracking guarantee. Visibly labeled "Estimated" in UI. |
| PII embedded in URL path segments not scrubbed in MVP | Accepted (Scoped) | Query strings stripped in MVP. Path-segment regex scrubber scheduled for post-MVP. |
| HMAC signing key security | Managed Control | Secret mounted out-of-band via Docker secrets / Key Vault; never in DB or appsettings. |

---

## 5. Consequences & Compliance Verification

### Positive Consequences
* Compliance-ready architectural narrative suitable for Swedish IMY / EU DPO audits.
* Complete elimination of raw IP address and User-Agent header storage across message queues and databases.
* Cryptographic protection against retroactive database compromise for authenticated user identities.
* Clear distinction in reporting between exact counts (Tier 2) and statistical estimates (Tier 1).

### Negative / Cost Consequences
* Multi-day anonymous unique visitor metrics are approximations rather than exact figures.
* Requires a custom PostgreSQL + TimescaleDB container image pre-packaged with the `hll` extension.
* Tier 2 authenticated visitors require an administrative Right to Erasure endpoint.

---

## 6. References
* [docs/spec.md](file:///c:/Programming/CSharp/Projects/privacy-first-analytics-dashboard/docs/spec.md) — Functional Requirements FR-2.1 & Risk Register
* GDPR Recital 26 (Notion of Pseudonymization and Anonymization)
* Swedish Authority for Privacy Protection (IMY) Guidelines on Google Analytics Enforcement
