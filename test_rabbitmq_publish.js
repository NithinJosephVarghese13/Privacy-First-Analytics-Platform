const http = require('http');

const data = JSON.stringify({
  vhost: "/",
  name: "analytics.events",
  properties: {
    delivery_mode: 2,
    content_type: "application/json"
  },
  routing_key: "event.received",
  payload: JSON.stringify({
    eventId: "11111111-1111-1111-1111-111111111111",
    timestamp: new Date().toISOString(),
    organizationId: "00000000-0000-0000-0000-000000000001",
    anonymousDailyHash: "test1234567890",
    durableHash: null,
    isAuthenticated: false,
    eventType: "pageview",
    path: "/rabbitmq-http-test"
  }),
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
  res.on('end', () => console.log('STATUS:', res.statusCode, 'BODY:', body));
});

req.write(data);
req.end();
