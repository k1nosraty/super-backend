\set QUIET on
BEGIN;
INSERT INTO jobs."Jobs" ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt", "FinishedAt")
SELECT gen_random_uuid(), 'fixture.history', '{}', 'succeeded', 1, 5, now() - interval '40 days', now() - interval '40 days'
FROM generate_series(1, :rows);
INSERT INTO jobs."Jobs" ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt")
SELECT gen_random_uuid(), 'notes.created', '{}', 'pending', 0, 5, now()
FROM generate_series(1, 25);
ANALYZE jobs."Jobs";
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT "Id" FROM jobs."Jobs"
WHERE "Type" = ANY(ARRAY['notes.created']::varchar[]) AND "Attempts" < "MaxAttempts"
  AND (("Status" = 'pending' AND "AvailableAt" <= now()) OR ("Status" = 'running' AND "LeaseUntil" <= now()))
ORDER BY "AvailableAt", "Id"
FOR UPDATE SKIP LOCKED
LIMIT 1;
ROLLBACK;
