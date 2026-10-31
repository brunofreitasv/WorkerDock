# Monitoring request and SQL load

## Intent

Reduce SQL queries and HTTP requests from multiple operators viewing tenant monitoring screens. Keep the existing tenant web application, separate runtime service, SQL database, and request/response APIs. Operators should still see live status within a predictable delay and retain manual refresh.

## Goals and limits

- Share the expensive dashboard snapshot and worker status reads among clients connected to the same web process.
- Coalesce simultaneous cache misses into one SQL query per snapshot.
- Reduce browser refresh frequency, especially on history screens.
- Keep authorization, no-store responses, stale-data warnings, and visibility pause behavior.
- Do not add SignalR, a broker, a database change feed, or cross-process cache coordination. A deployment with multiple web processes gets one small cache per process.
- Do not promise a specific capacity gain without measuring deployed HTTP and SQL load.

## Approaches

1. **Tune polling only:** few changes and fewer HTTP/SQL requests, but SQL work still grows linearly with client count.
2. **Tune polling plus a short server cache (chosen):** lowers browser requests and shares the common dashboard/worker SQL reads. It adds bounded staleness and a small in-process concurrency component.
3. **SignalR:** can reduce browser requests further, but this deployment's runtime writes SQL in a separate process. Reliable notifications would require a change feed or another runtime-to-web path, plus reconnect and time-based refresh behavior.

## Behavior

- The shared visible polling hook accepts a per-view interval, with the existing five-second interval as a default for untouched consumers. It retains the immediate first load, refresh on tab becoming visible, no overlapping requests, manual refresh, and exponential error backoff capped at one minute.
- Dashboard and worker status poll every 15 seconds; job monitoring and execution details every 15 seconds; active incidents every 15 seconds; alert deliveries every 30 seconds. Execution history and audit poll every 60 seconds. A user changing filters or pages fetches immediately; manual refresh remains immediate. The alerts screen has two independently refreshed tables.
- Only `GET /api/dashboard` and `GET /api/monitoring/workers` use server-side cache. Each has one process-wide value with a five-second absolute lifetime from successful query completion. An expired value is not served. Simultaneous misses share one in-flight query. Successful results alone enter the cache; failures return the existing 503 behavior and permit the next request to retry. Disconnecting one HTTP caller must not cancel a query shared with other callers.
- The cache sits behind the existing read authorization on each endpoint. It does not emit browser-cacheable responses or store user-specific data. The tenant web process serves only its configured tenant database. A restarted web process starts with an empty cache.
- A dashboard or worker response can be up to five seconds old when fetched; the next browser poll determines when it appears on screen. Manual refresh may receive the same cached value during that window. The dashboard's time-dependent health and 24-hour counts therefore continue to be recomputed periodically even when SQL rows do not change.

## Verification

- Test per-view polling timing, visibility resume, backoff, and manual refresh.
- Test cache hit, expiry, simultaneous misses, failed loads, and one disconnected caller while another waits.
- Confirm read authorization and `no-store` handling remain at the HTTP boundary.
- Measure before/after request rate and SQL query rate per endpoint under multiple connected clients. Treat the expected reduction as an estimate until measured.

## Builder calls

Use the .NET web host's built-in in-memory services for the two bounded snapshots. Do not cache filtered or cursor-paged results because their keys vary by operator. Keep current API DTOs and route paths.
