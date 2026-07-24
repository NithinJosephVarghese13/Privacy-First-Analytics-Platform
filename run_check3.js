const http = require('http');
const { execSync } = require('child_process');

function exec(cmd) {
  return execSync(cmd, { encoding: 'utf8' }).trim();
}

function publishToRabbitMq(message) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify({
      vhost: "/",
      name: "analytics.events",
      properties: {
        delivery_mode: 2,
        content_type: "application/json"
      },
      routing_key: "event.received",
      payload: JSON.stringify(message),
      payload_encoding: "string"
    });

    const req = http.request({
      hostname: 'localhost',
      port: 15672,
      path: '/api/exchanges/%2F/analytics.events/publish',
      method: 'POST',
      auth: 'analytics:analytics_dev',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(data)
      }
    }, res => {
      let body = '';
      res.on('data', chunk => body += chunk);
      res.on('end', () => resolve(res.statusCode === 200));
    });

    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

function generateUuid(index) {
  const hex = index.toString(16).padStart(12, '0');
  return `33333333-3333-3333-3333-${hex}`;
}

async function publishBatch(prefix, count) {
  console.log(`Publishing ${count} messages directly to RabbitMQ queue with prefix '${prefix}'...`);
  const orgId = "00000000-0000-0000-0000-000000000001";
  
  for (let i = 0; i < count; i++) {
    const eventId = generateUuid(i);
    const msg = {
      eventId: eventId,
      timestamp: new Date().toISOString(),
      organizationId: orgId,
      anonymousDailyHash: "check3_hash_" + i,
      durableHash: null,
      isAuthenticated: false,
      eventType: "pageview",
      path: `/${prefix}-${i}`
    };
    await publishToRabbitMq(msg);
  }
  console.log(`Published ${count} messages successfully.`);
}

async function run() {
  console.log("=== CHECK 3: RabbitMQ Direct Publish & Postgres Kill/Restart Resilience ===");

  // --- Part A: Direct 500 batch ---
  console.log("\n--- PART A: Publish 500 messages directly to RabbitMQ (bypassing API) ---");
  exec("docker stop analytics-worker");
  exec("docker exec analytics-rabbitmq rabbitmqctl purge_queue analytics.events.queue");
  exec("docker exec analytics-postgres psql -U analytics -d analytics -c \"DELETE FROM analytics_events WHERE path LIKE '/check3-direct-%';\"");

  await publishBatch("check3-direct", 500);

  console.log("Starting analytics-worker container...");
  exec("docker start analytics-worker");

  console.log("Waiting 6 seconds for worker to process 500 messages...");
  await new Promise(r => setTimeout(r, 6000));

  const directCountStr = exec("docker exec analytics-postgres psql -U analytics -d analytics -t -A -c \"SELECT COUNT(*) FROM analytics_events WHERE path LIKE '/check3-direct-%';\"");
  const directCount = parseInt(directCountStr, 10);
  console.log(`Direct 500 messages count in analytics_events table: ${directCount} / 500`);

  // --- Part B: Kill Postgres mid-batch & restart ---
  console.log("\n--- PART B: Kill Postgres container mid-batch, start worker, restart Postgres ---");
  exec("docker stop analytics-worker");
  exec("docker exec analytics-rabbitmq rabbitmqctl purge_queue analytics.events.queue");
  exec("docker exec analytics-postgres psql -U analytics -d analytics -c \"DELETE FROM analytics_events WHERE path LIKE '/check3-requeue-%';\"");

  await publishBatch("check3-requeue", 500);

  console.log("Killing Postgres container (docker kill analytics-postgres)...");
  exec("docker kill analytics-postgres");

  console.log("Starting worker with Postgres down (worker will encounter connection error)...");
  exec("docker start analytics-worker");

  console.log("Waiting 4 seconds while worker attempts connection to dead Postgres...");
  await new Promise(r => setTimeout(r, 4000));

  console.log("Restarting Postgres container (docker start analytics-postgres)...");
  exec("docker start analytics-postgres");

  console.log("Waiting 12 seconds for Postgres to become healthy and worker to retry and flush...");
  await new Promise(r => setTimeout(r, 12000));

  const requeueCountStr = exec("docker exec analytics-postgres psql -U analytics -d analytics -t -A -c \"SELECT COUNT(*) FROM analytics_events WHERE path LIKE '/check3-requeue-%';\"");
  const requeueCount = parseInt(requeueCountStr, 10);
  console.log(`Requeued 500 messages count in analytics_events table after Postgres restart: ${requeueCount} / 500`);

  const pass = (directCount === 500 && requeueCount === 500);
  console.log(`\nCheck 3 Result: ${pass ? "PASS" : "FAIL"}`);
}

run().catch(console.error);
