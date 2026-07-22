# K6 Ingestion Load Test

This folder contains the K6 load testing script for validating the ingestion latency SLA of the `POST /api/v1/track` endpoint under load.

## Performance Requirements (NFR-1)

- **Target Throughput**: 200 requests/second sustained for 60 seconds.
- **Latency Threshold**: p95 ingestion latency `< 50ms`.
- **Success Rate Threshold**: HTTP error rate `< 1.0%` (Success rate `> 99.0%`).

## Prerequisites

1. Install [k6](https://k6.io/docs/getting-started/installation/):
   ```bash
   # Windows (via winget or choco)
   winget install k6 --source winget
   # OR macOS
   brew install k6
   ```
   Alternatively, you can run K6 via Docker.

2. Ensure the API backend is running (e.g. at `http://localhost:5000` or `http://localhost:5001`).

3. Ensure valid Organization Public Write Keys exist in the database corresponding to the `TENANT_KEYS` used by the test.

## Seed Test Organizations (PostgreSQL)

If running against a clean database, seed test organizations matching the default write keys in `track-load-test.js`:

```sql
INSERT INTO organizations (org_id, name, public_write_key) VALUES
  (gen_random_uuid(), 'Load Test Org 1', '11111111-1111-1111-1111-111111111111'),
  (gen_random_uuid(), 'Load Test Org 2', '22222222-2222-2222-2222-222222222222'),
  (gen_random_uuid(), 'Load Test Org 3', '33333333-3333-3333-3333-333333333333'),
  (gen_random_uuid(), 'Load Test Org 4', '44444444-4444-4444-4444-444444444444'),
  (gen_random_uuid(), 'Load Test Org 5', '55555555-5555-5555-5555-555555555555')
ON CONFLICT (public_write_key) DO NOTHING;
```

## Running the Load Test

### Local K6 CLI

```bash
k6 run k6/track-load-test.js
```

### With Custom Parameters

```bash
k6 run -e BASE_URL="http://localhost:5000" -e TENANT_KEYS="key1-guid,key2-guid,key3-guid" k6/track-load-test.js
```

### Via Docker

```bash
docker run --rm -i --net=host grafana/k6 run - < k6/track-load-test.js
```

## Metrics & Threshold Outputs

K6 will output performance summary metrics at the end of the test run:

- `http_req_duration`: Displays average, min, med, max, `p(90)`, and `p(95)` latencies. Must pass `p(95) < 50ms`.
- `http_req_failed`: Percentage of failed HTTP requests. Must pass `rate < 0.01` (<1%).
- `tracking_success_rate`: Custom rate tracking 202 Accepted responses. Must pass `rate > 0.99`.
- `tracking_latency`: Custom trend tracking request duration.
