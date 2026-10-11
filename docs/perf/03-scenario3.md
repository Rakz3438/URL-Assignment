# Scenario 3 measurements (PostgreSQL + Redis + Kafka + worker, 50 VUs, rate limits off for capacity runs)

## Normal load, telemetry export off
```
     http_req_duration..............: avg=2.26ms  p(50)=2.08ms p(95)=3.66ms  p(99)=6.16ms  max=36.09ms
   ✓ http_req_failed................: 0.00%   0 out of 432325
     http_reqs......................: 432325  21615.170958/s
```

## Normal load, 100% traces exported to Aspire dashboard
```
     http_req_duration..............: avg=2.6ms  p(50)=2.32ms p(95)=4.57ms  p(99)=8.19ms   max=33.73ms
   ✓ http_req_failed................: 0.00%   0 out of 376896
     http_reqs......................: 376896  18842.040626/s
```

## Redis down 10 s — breaker with 10 s sampling window (mis-tuned)
```
     http_req_duration..............: avg=8.12ms  p(50)=4.48ms p(95)=11.53ms p(99)=205.54ms max=233.79ms
   ✓ http_req_failed................: 0.00%   0 out of 152938
     http_reqs......................: 152938  6116.319326/s
```

## Redis down 10 s — breaker with 2 s window
```
     http_req_duration..............: avg=3.16ms  p(50)=2.23ms p(95)=7.1ms   p(99)=9.75ms   max=270.84ms
   ✓ http_req_failed................: 0.00%   0 out of 624308
     http_reqs......................: 624308  15607.45682/s
```

## Redis down 10 s — 2 s window + throttled logging (final)
```
     http_req_duration..............: avg=3.25ms  p(50)=2.26ms p(95)=6.66ms  p(99)=11.52ms  max=218.11ms
   ✓ http_req_failed................: 0.00%   0 out of 530592
     http_reqs......................: 530592  15159.116055/s
```

## PostgreSQL down 10 s (final)
```
     http_req_duration..............: avg=2.02ms  p(50)=1.89ms p(95)=3.16ms p(99)=4.99ms   max=14.65ms
   ✓ http_req_failed................: 0.00%   0 out of 604680
     http_reqs......................: 604680  24186.125776/s
```

