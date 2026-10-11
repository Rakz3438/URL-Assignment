# Baseline (synchronous analytics, PostgreSQL 16, no cache) — 50 VUs, 30 s
```
     checks.........................: 100.00% 222854 out of 222854
     http_req_duration..............: avg=6.66ms  p(50)=6.28ms  p(95)=9.64ms  p(99)=13.69ms  max=140.33ms
   ✓ http_req_failed................: 0.00%   0 out of 222854
     http_reqs......................: 222854  7427.604786/s
```

# Baseline under incident — click_events locked 10 s during a 20 s run
```
     checks.........................: 100.00% 48102 out of 48102
     http_req_duration..............: avg=20.72ms p(50)=6.24ms  p(95)=17.47ms p(99)=84.72ms  max=10.03s 
   ✓ http_req_failed................: 0.00%   0 out of 48102
     http_reqs......................: 48102   2404.186044/s
```
