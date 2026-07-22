import http from 'k6/http';
import { check } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Custom metrics for explicit tracking and summary outputs
const trackingSuccessRate = new Rate('tracking_success_rate');
const trackingLatency = new Trend('tracking_latency');

// Environment configurations
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const TRACK_ENDPOINT = `${BASE_URL}/api/v1/track`;

// Default write keys (can be overridden via __ENV.TENANT_KEYS="guid1,guid2,guid3,guid4")
// Multiple tenant keys are used to distribute the 200 req/sec total load across tenants,
// respecting the 100 req/sec per-tenant token bucket rate limiter on POST /api/v1/track.
const DEFAULT_TENANT_KEYS = [
  '11111111-1111-1111-1111-111111111111',
  '22222222-2222-2222-2222-222222222222',
  '33333333-3333-3333-3333-333333333333',
  '44444444-4444-4444-4444-444444444444',
  '55555555-5555-5555-5555-555555555555',
];

const TENANT_KEYS = __ENV.TENANT_KEYS
  ? __ENV.TENANT_KEYS.split(',').map((k) => k.trim())
  : DEFAULT_TENANT_KEYS;

// Realistic varied payload options
const URL_PATHS = [
  '/home',
  '/pricing?utm_source=google&utm_medium=cpc&ref=campaign_123',
  '/checkout/confirmation?order_id=9876&email=user%40example.com',
  '/docs/getting-started?token=secret_token_12345',
  '/blog/privacy-first-analytics-v1',
  '/features/dashboard?view=analytics&sort=desc',
  '/settings/profile?user_id=12345',
  '/contact-us',
  '/products/saas-dashboard?category=software&promo=summer2026',
  '/about'
];

const EVENT_TYPES = [
  'pageview',
  'click',
  'conversion',
  'form_submit',
  'scroll_50',
  'download'
];

const REFERRAL_SOURCES = [
  'https://google.com/search?q=privacy+analytics',
  'https://news.ycombinator.com',
  'https://github.com/NithinJosephVarghese13',
  'https://twitter.com/privacy_first',
  'https://linkedin.com',
  'direct',
  ''
];

const USER_AGENTS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
  'Mozilla/5.0 (X11; Linux x86_64; rv:109.0) Gecko/20100101 Firefox/121.0',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'
];

const ORIGINS = [
  'https://app.example.com',
  'https://dashboard.example.org',
  'https://mysite.io',
  'https://store.acme.com'
];

export const options = {
  scenarios: {
    ingestion_load_test: {
      executor: 'constant-arrival-rate',
      rate: 200,            // 200 req/sec sustained target (NFR-1 claim)
      timeUnit: '1s',       // Rate per 1 second
      duration: '60s',      // Sustained test duration of 60 seconds
      preAllocatedVUs: 50,  // Initial pool of VUs
      maxVUs: 200,          // Maximum VUs allowed to scale up under load
    },
  },
  thresholds: {
    // Fail test if p95 latency exceeds 50ms (NFR-1 requirement)
    http_req_duration: ['p(95)<50'],
    // Fail test if error rate exceeds 1% (NFR-1 requirement)
    http_req_failed: ['rate<0.01'],
    // Custom metrics thresholds
    tracking_success_rate: ['rate>0.99'],
    tracking_latency: ['p(95)<50'],
  },
};

// Helper function to randomly select an element from an array
function getRandomElement(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

export default function () {
  const tenantKey = getRandomElement(TENANT_KEYS);
  const path = getRandomElement(URL_PATHS);
  const eventType = getRandomElement(EVENT_TYPES);
  const referralSource = getRandomElement(REFERRAL_SOURCES);
  const userAgent = getRandomElement(USER_AGENTS);
  const origin = getRandomElement(ORIGINS);

  const fullUrl = `${origin}${path}`;

  const payload = JSON.stringify({
    url: fullUrl,
    eventType: eventType,
    referralSource: referralSource || null
  });

  const clientIp = `192.168.1.${Math.floor(Math.random() * 254) + 1}`;

  const params = {
    headers: {
      'Content-Type': 'application/json',
      'X-Tenant-Id': tenantKey,
      'X-Tenant-Origin': origin,
      'X-Forwarded-For': clientIp,
      'User-Agent': userAgent,
    },
  };

  const response = http.post(TRACK_ENDPOINT, payload, params);

  const isAccepted = response.status === 202;
  trackingSuccessRate.add(isAccepted);
  trackingLatency.add(response.timings.duration);

  check(response, {
    'status is 202 Accepted': (r) => r.status === 202,
    'latency under 50ms': (r) => r.timings.duration < 50,
  });
}
