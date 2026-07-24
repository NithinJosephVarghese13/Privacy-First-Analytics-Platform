import http from 'k6/http';
import { Counter } from 'k6/metrics';

const tenant1_202 = new Counter('tenant1_202');
const tenant1_429 = new Counter('tenant1_429');
const tenant2_202 = new Counter('tenant2_202');
const tenant2_429 = new Counter('tenant2_429');

export const options = {
  scenarios: {
    tenant1_heavy_load: {
      executor: 'constant-arrival-rate',
      rate: 150,
      timeUnit: '1s',
      duration: '5s',
      preAllocatedVUs: 50,
      maxVUs: 150,
      exec: 'tenant1Task',
    },
    tenant2_normal_load: {
      executor: 'constant-arrival-rate',
      rate: 50,
      timeUnit: '1s',
      duration: '5s',
      preAllocatedVUs: 20,
      maxVUs: 50,
      exec: 'tenant2Task',
    },
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5115';

export function tenant1Task() {
  const params = {
    headers: {
      'Content-Type': 'application/json',
      'X-Tenant-Id': '00000000-0000-0000-0000-000000000001',
      'X-Forwarded-For': '192.168.1.10',
      'User-Agent': 'K6-Tenant1/1.0',
    },
  };
  const payload = JSON.stringify({ url: 'https://example.com/t1', eventType: 'pageview' });
  const res = http.post(`${BASE_URL}/api/v1/track`, payload, params);

  if (res.status === 202) tenant1_202.add(1);
  else if (res.status === 429) tenant1_429.add(1);
}

export function tenant2Task() {
  const params = {
    headers: {
      'Content-Type': 'application/json',
      'X-Tenant-Id': '00000000-0000-0000-0000-000000000002',
      'X-Forwarded-For': '192.168.1.20',
      'User-Agent': 'K6-Tenant2/1.0',
    },
  };
  const payload = JSON.stringify({ url: 'https://example.com/t2', eventType: 'pageview' });
  const res = http.post(`${BASE_URL}/api/v1/track`, payload, params);

  if (res.status === 202) tenant2_202.add(1);
  else if (res.status === 429) tenant2_429.add(1);
}
