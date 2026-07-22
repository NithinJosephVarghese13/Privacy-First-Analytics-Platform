# Ingestion Load Test Demo Summary & Verified Results

**Task**: Ingestion Latency SLA Validation (`POST /api/v1/track`)  
**Target SLA**: `< 50ms` (p95 latency) under **200 req/sec** sustained for 60 seconds.  
**Execution Environment**: Full Local Stack (ASP.NET Core 10 Web API + PostgreSQL/TimescaleDB + RabbitMQ + Docker).  
**Timestamp**: 2026-07-22

---

## Benchmark Summary

| Metric | Measured Result | SLA / Threshold Target | Status |
|---|---|---|---|
| **Total Requests** | `12,000` | 12,000 (200 req/s * 60s) | PASSED |
| **Sustained Throughput** | `200.01 req/sec` | 200.00 req/sec | PASSED |
| **Success Rate** | `100.00%` (12,000 / 12,000) | `> 99.00%` | **PASSED** |
| **Failed Requests** | `0.00%` (0 / 12,000) | `< 1.00%` | **PASSED** |
| **p95 Ingestion Latency** | **`3.24 ms`** | **`< 50.00 ms`** | **PASSED (15x headroom)** |
| **p90 Ingestion Latency** | `2.87 ms` | - | - |
| **Average Latency** | `2.23 ms` | - | - |
| **Median Latency** | `2.09 ms` | - | - |
| **Max Latency** | `27.86 ms` | `< 50.00 ms` | PASSED |

---

## K6 Execution Output Transcript

```text
  █ THRESHOLDS 

    http_req_duration
    ✓ 'p(95)<50' p(95)=3.24ms

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%

    tracking_latency
    ✓ 'p(95)<50' p(95)=3.249654

    tracking_success_rate
    ✓ 'rate>0.99' rate=100.00%


  █ TOTAL RESULTS 

    checks_total.......: 24000   400.011536/s
    checks_succeeded...: 100.00% 24000 out of 24000
    checks_failed......: 0.00%   0 out of 24000

    ✓ status is 202 Accepted
    ✓ latency under 50ms

    CUSTOM
    tracking_latency...............: avg=2.234204 min=0.498544 med=2.090719 max=27.860421 p(90)=2.876883 p(95)=3.249654
    tracking_success_rate..........: 100.00% 12000 out of 12000

    HTTP
    http_req_duration..............: avg=2.23ms   min=498.54µs med=2.09ms   max=27.86ms   p(90)=2.87ms   p(95)=3.24ms  
      { expected_response:true }...: avg=2.23ms   min=498.54µs med=2.09ms   max=27.86ms   p(90)=2.87ms   p(95)=3.24ms  
    http_req_failed................: 0.00%   0 out of 12000
    http_reqs......................: 12000   200.005768/s

    EXECUTION
    iteration_duration.............: avg=2.46ms   min=1.68ms   med=2.3ms    max=30.32ms   p(90)=3.16ms   p(95)=3.56ms  
    iterations.....................: 12000   200.005768/s
    vus............................: 1       min=0              max=2 
    vus_max........................: 50      min=50             max=50

    NETWORK
    data_received..................: 1.2 MB  20 kB/s
    data_sent......................: 6.0 MB  100 kB/s

running (1m00.0s), 000/050 VUs, 12000 complete and 0 interrupted iterations
ingestion_load_test ✓ [ 100% ] 000/050 VUs  1m0s  200.00 iters/s
```

---

## Key Performance Findings

1. **SLA Compliance**: `3.24ms` p95 ingestion latency vs `< 50ms` target provides **~15x headroom**.
2. **Zero Failures**: `100.00%` success rate (12,000 / 12,000 requests accepted with `202 Accepted`).
3. **Multi-Tenant Token Bucket Isolation**: Rotating requests across 5 tenant write keys successfully kept each tenant under the 100 req/sec rate limiter threshold while driving a sustained aggregate load of 200 req/sec.
4. **Scrubbing & Identity Overhead**: The execution of in-memory URL scrubbing (stripping query string PII) and two-tier pseudonym computing per request added negligible overhead (< 2.5ms average latency).
