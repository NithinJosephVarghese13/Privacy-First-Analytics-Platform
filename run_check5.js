const http = require('http');

const agent = new http.Agent({
  keepAlive: true,
  maxSockets: 500,
  maxFreeSockets: 200
});

function sendTrackRequest(tenantId) {
  return new Promise((resolve) => {
    const data = JSON.stringify({
      url: 'https://example.com/rate-limit-test',
      eventType: 'pageview'
    });

    const req = http.request({
      hostname: 'localhost',
      port: 5115,
      path: '/api/v1/track',
      method: 'POST',
      agent: agent,
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(data),
        'X-Tenant-Id': tenantId,
        'X-Forwarded-For': '192.168.1.100',
        'User-Agent': 'RateLimitAgent/1.0'
      }
    }, res => {
      resolve(res.statusCode);
    });

    req.on('error', () => resolve(500));
    req.write(data);
    req.end();
  });
}

async function runStream(tenantId, batchCount, intervalMs, durationMs) {
  const promises = [];
  const endTime = Date.now() + durationMs;

  while (Date.now() < endTime) {
    const loopStart = Date.now();
    for (let i = 0; i < batchCount; i++) {
      promises.push(sendTrackRequest(tenantId));
    }
    const elapsed = Date.now() - loopStart;
    const delay = Math.max(1, intervalMs - elapsed);
    await new Promise(r => setTimeout(r, delay));
  }

  const statuses = await Promise.all(promises);
  const results = { status202: 0, status429: 0, other: 0, total: statuses.length, statusCounts: {} };
  for (const status of statuses) {
    results.statusCounts[status] = (results.statusCounts[status] || 0) + 1;
    if (status === 202) results.status202++;
    else if (status === 429) results.status429++;
    else results.other++;
  }

  return results;
}

async function run() {
  console.log("=== CHECK 5: Per-Tenant-Origin Rate Limiting & Multi-Tenant Isolation ===");
  console.log("Warming up connection pool...");
  await Promise.all(Array.from({ length: 20 }, () => sendTrackRequest("00000000-0000-0000-0000-000000000001")));
  await new Promise(r => setTimeout(r, 500));

  console.log("Firing 150 req/sec for Tenant 1 and 50 req/sec for Tenant 2 concurrently for 5 seconds...\n");

  const tenant1 = "00000000-0000-0000-0000-000000000001";
  const tenant2 = "00000000-0000-0000-0000-000000000002";

  // Every 20ms: 3 req for Tenant 1 (150 req/s), 1 req for Tenant 2 (50 req/s)
  const durationMs = 5000;
  const [t1Results, t2Results] = await Promise.all([
    runStream(tenant1, 3, 20, durationMs),
    runStream(tenant2, 1, 20, durationMs)
  ]);

  console.log("--- Tenant 1 Results (Target: 150 req/sec, 5s) ---");
  console.log(`Total Requests Sent & Measured: ${t1Results.total}`);
  console.log(`Status breakdown:`, t1Results.statusCounts);
  console.log(`202 Accepted (Succeeded): ${t1Results.status202} (~${Math.round(t1Results.status202 / 5)}/sec)`);
  console.log(`429 Too Many Requests (Throttled): ${t1Results.status429} (~${Math.round(t1Results.status429 / 5)}/sec)`);

  console.log("\n--- Tenant 2 Results (Target: 50 req/sec, 5s - Concurrent) ---");
  console.log(`Total Requests Sent & Measured: ${t2Results.total}`);
  console.log(`Status breakdown:`, t2Results.statusCounts);
  console.log(`202 Accepted (Succeeded): ${t2Results.status202}`);
  console.log(`429 Too Many Requests (Throttled): ${t2Results.status429}`);

  const t1Capped = (t1Results.status429 > 0 && Math.abs(t1Results.status202 / 5 - 100) < 35);
  const t2Unthrottled = (t2Results.status429 === 0 && t2Results.status202 === t2Results.total);

  console.log(`\nTenant 1 Throttling Cap (~100/sec): ${t1Capped ? "PASS" : "FAIL"}`);
  console.log(`Tenant 2 Multi-Tenant Isolation (Unthrottled): ${t2Unthrottled ? "PASS" : "FAIL"}`);
  console.log(`\nCheck 5 Result: ${t1Capped && t2Unthrottled ? "PASS" : "FAIL"}`);
}

run().catch(console.error);
