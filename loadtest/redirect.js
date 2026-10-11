// k6 load test for the redirect hot path (NFR-1).
// Run (no local install needed):
//   docker run --rm -i -e BASE_URL=http://host.docker.internal:5080 -e CODE=<code> grafana/k6 run - < loadtest/redirect.js
import http from 'k6/http';
import { check } from 'k6';

export const options = {
  scenarios: {
    redirects: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 50),
      duration: __ENV.DURATION || '30s',
    },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
  },
};

const BASE = __ENV.BASE_URL || 'http://localhost:5080';
const CODE = __ENV.CODE;

export default function () {
  const res = http.get(`${BASE}/${CODE}`, {
    redirects: 0,
    headers: { 'User-Agent': 'k6-loadtest', Referer: 'https://loadtest.example/' },
  });
  check(res, { 'is 302': (r) => r.status === 302 });
}
