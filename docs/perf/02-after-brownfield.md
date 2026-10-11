# After (Kafka + Redis + Analytics Worker, PostgreSQL 16) — 50 VUs

## Normal load, 30 s
```
     http_req_duration..............: avg=1.97ms  p(50)=1.84ms p(95)=3.1ms   p(99)=5ms      max=28.29ms
   ✓ http_req_failed................: 0.00%   0 out of 743343
     http_reqs......................: 743343  24800.392004/s
```

## Chaos 1: click_events locked 10 s (20 s run)
```
     http_req_duration..............: avg=1.93ms  p(50)=1.8ms  p(95)=3.03ms  p(99)=4.31ms   max=34.9ms 
   ✓ http_req_failed................: 0.00%   0 out of 507608
     http_reqs......................: 507608  25378.930812/s
```

## Chaos 2, FIRST VERSION: Kafka stopped 10 s (25 s run) — found the head-of-line blocking bug
```
     http_req_duration..............: avg=1.95ms  p(50)=1.82ms p(95)=3.03ms  p(99)=4.73ms   max=14.44ms
   ✓ http_req_failed................: 0.00%   0 out of 625796
     http_reqs......................: 625796  25031.089111/s
```

## Chaos 2, AFTER FIX: Kafka stopped 10 s (25 s run)
```
     http_req_duration..............: avg=1.97ms p(50)=1.77ms p(95)=3.42ms  p(99)=5.47ms   max=61.09ms
   ✓ http_req_failed................: 0.00%   0 out of 620535
     http_reqs......................: 620535  24820.650502/s
```

## Chaos 3: Redis stopped 10 s (25 s run)
```
     http_req_duration..............: avg=3.36ms  p(50)=1.81ms p(95)=3.17ms  p(99)=6.88ms   max=275.91ms
   ✓ http_req_failed................: 0.00%   0 out of 367397
     http_reqs......................: 367397  14695.600785/s
```

## Chaos 4: Analytics Worker SIGKILL at 8 s, restarted at 12 s (20 s run)
```
     http_req_duration..............: avg=1.87ms  p(50)=1.76ms p(95)=2.86ms  p(99)=4.39ms  max=12.68ms
   ✓ http_req_failed................: 0.00%   0 out of 521780
     http_reqs......................: 521780  26087.564322/s
```

