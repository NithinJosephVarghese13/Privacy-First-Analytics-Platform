const http = require('http');
const { execSync } = require('child_process');

function queryDb(sql) {
  const cmd = `docker exec analytics-postgres psql -U analytics -d analytics -t -A -c "${sql.replace(/"/g, '\\"')}"`;
  return execSync(cmd, { encoding: 'utf8' }).trim();
}

function sendTrackRequest(headers, payload) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(payload);
    const options = {
      hostname: 'localhost',
      port: 5115,
      path: '/api/v1/track',
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(data),
        ...headers
      }
    };

    const req = http.request(options, res => {
      let body = '';
      res.on('data', chunk => body += chunk);
      res.on('end', () => resolve({ statusCode: res.statusCode, body }));
    });

    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

async function run() {
  console.log("=== CHECK 1: URL Query String Scrubbing ===");
  const check1Url = "https://example.com/checkout/confirmation?email=alice%40example.com&token=secret123";
  const check1Res = await sendTrackRequest({
    'X-Tenant-Id': '00000000-0000-0000-0000-000000000001'
  }, {
    url: check1Url,
    eventType: 'pageview'
  });
  console.log("POST /api/v1/track Status:", check1Res.statusCode);

  // Wait for worker to flush
  await new Promise(r => setTimeout(r, 4000));

  const check1Row = queryDb("SELECT path FROM analytics_events WHERE path LIKE '%checkout/confirmation%' ORDER BY timestamp DESC LIMIT 1;");
  console.log("Persisted Path in DB:", check1Row);
  const check1Pass = (check1Res.statusCode === 202 && check1Row === "https://example.com/checkout/confirmation");
  console.log("Check 1 Result:", check1Pass ? "PASS" : "FAIL");

  console.log("\n=== CHECK 2: Two-Tier Identity Model ===");
  const ip = "203.0.113.88";
  const ua = "Check2Agent/1.0";
  const tenantId = "00000000-0000-0000-0000-000000000001";

  // 1. First anonymous request
  const anon1Res = await sendTrackRequest({
    'X-Tenant-Id': tenantId,
    'X-Forwarded-For': ip,
    'User-Agent': ua
  }, {
    url: "https://example.com/check2-anon-req-1",
    eventType: "pageview"
  });
  console.log("Anon Req 1 Status:", anon1Res.statusCode);

  // 2. Second anonymous request (same IP/UA/Day)
  const anon2Res = await sendTrackRequest({
    'X-Tenant-Id': tenantId,
    'X-Forwarded-For': ip,
    'User-Agent': ua
  }, {
    url: "https://example.com/check2-anon-req-2",
    eventType: "pageview"
  });
  console.log("Anon Req 2 Status:", anon2Res.statusCode);

  // 3. Authenticated request (same IP/UA, but with user ID and opted in)
  const authRes = await sendTrackRequest({
    'X-Tenant-Id': tenantId,
    'X-Forwarded-For': ip,
    'User-Agent': ua,
    'X-User-Id': 'user-check2-test-42',
    'X-User-Opted-In': 'true'
  }, {
    url: "https://example.com/check2-auth-req-1",
    eventType: "pageview"
  });
  console.log("Auth Req Status:", authRes.statusCode);

  // Wait for worker to flush
  await new Promise(r => setTimeout(r, 4000));

  const anon1Hash = queryDb("SELECT anonymous_daily_hash FROM analytics_events WHERE path = 'https://example.com/check2-anon-req-1' ORDER BY timestamp DESC LIMIT 1;");
  const anon2Hash = queryDb("SELECT anonymous_daily_hash FROM analytics_events WHERE path = 'https://example.com/check2-anon-req-2' ORDER BY timestamp DESC LIMIT 1;");

  console.log("Anon Req 1 Hash:", anon1Hash);
  console.log("Anon Req 2 Hash:", anon2Hash);
  console.log("Hashes Identical:", anon1Hash === anon2Hash && anon1Hash !== "");

  const authAnonHash = queryDb("SELECT anonymous_daily_hash FROM analytics_events WHERE path = 'https://example.com/check2-auth-req-1' ORDER BY timestamp DESC LIMIT 1;");
  const authDurableHash = queryDb("SELECT durable_hash FROM analytics_events WHERE path = 'https://example.com/check2-auth-req-1' ORDER BY timestamp DESC LIMIT 1;");

  console.log("Auth Req AnonymousDailyHash (expect empty/null):", authAnonHash || "(null)");
  console.log("Auth Req DurableHash (expect populated):", authDurableHash);

  const check2Pass = (anon1Hash === anon2Hash && anon1Hash.length > 0 && authAnonHash === "" && authDurableHash.length > 0);
  console.log("Check 2 Result:", check2Pass ? "PASS" : "FAIL");
}

run().catch(console.error);
