#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
umask 077
scratch="$(mktemp -d)"
project="${REHEARSAL_PROJECT:-superbackend-rehearsal}"
export HTTPS_PORT="${REHEARSAL_PORT:-15443}" PUBLIC_HOST=localhost
export API_IMAGE="${API_IMAGE:-superbackend-rehearsal:local}"
export PUBLIC_ORIGIN="https://localhost:$HTTPS_PORT" TLS_DIRECTORY="$scratch/tls"
export EVIDENCE_DIRECTORY="$scratch"
export DB_BOOTSTRAP_PASSWORD="$(openssl rand -hex 24)"
export DB_MIGRATOR_PASSWORD="$(openssl rand -hex 24)"
export DB_RUNTIME_PASSWORD="$(openssl rand -hex 24)"
export DB_TENANT_PASSWORD="$(openssl rand -hex 24)"
export REHEARSAL_PASSWORD="$(openssl rand -hex 24)"
export MIGRATOR_CONNECTION="Host=database;Database=superbackend;Username=sb_migrator;Password=$DB_MIGRATOR_PASSWORD;GSS Encryption Mode=Disable"
export RUNTIME_CONNECTION="Host=database;Database=superbackend;Username=sb_runtime;Password=$DB_RUNTIME_PASSWORD;GSS Encryption Mode=Disable;Maximum Pool Size=10"
export TENANT_CONNECTION="Host=database;Database=superbackend;Username=sb_tenant;Password=$DB_TENANT_PASSWORD;GSS Encryption Mode=Disable"
export SMTP_HOST=mail.invalid SMTP_FROM=operator@example.test
export MODULE_NOTES=true MODULE_JOBS=true MODULE_OUTBOX=true MODULE_TENANCY=true PUBLISH_NOTE_JOBS=true
export QUEUE_MAIL=true

compose() { docker compose -p "$project" -f deploy/reference/compose.yaml "$@"; }
if [[ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$project" ||
    printf 'unavailable')" ]] || [[ -n "$(docker volume ls -q --filter "label=com.docker.compose.project=$project")" ]]; then
    echo "Choose an unused rehearsal project name." >&2
    rm -rf -- "$scratch"
    exit 2
fi

cleanup() {
    result=$?
    if [[ $result != 0 ]]; then compose ps -a >&2 || true; fi
    compose down -v >/dev/null 2>&1 || true
    rm -rf -- "$scratch"
}
trap cleanup EXIT
mkdir -p "$TLS_DIRECTORY"
openssl req -new -x509 -nodes -newkey rsa:2048 -days 2 -subj "/CN=localhost" \
    -addext "subjectAltName=DNS:localhost" -keyout "$TLS_DIRECTORY/key.pem" -out "$TLS_DIRECTORY/cert.pem" 2>/dev/null
if ! docker image inspect "$API_IMAGE" >/dev/null 2>&1; then
    ./scripts/build-release-image.sh "$root" "$API_IMAGE"
fi

if [[ -n "${PREVIOUS_IMAGE:-}" ]]; then
    API_IMAGE="$PREVIOUS_IMAGE" compose up -d --wait database
    API_IMAGE="$PREVIOUS_IMAGE" compose run --rm --no-deps migrate
    compose exec -T database psql -v ON_ERROR_STOP=1 -U sb_bootstrap -d superbackend <<'SQL'
INSERT INTO notes."Notes" ("Id", "OwnerId", "Title", "Body", "CreatedAt", "Version")
VALUES ('02000000-0000-0000-0000-000000000001', 'upgrade-fixture', 'Previous image fixture', '', now(), gen_random_uuid());
SQL
    printf 'Upgrading from previous image %s\n' "$PREVIOUS_IMAGE"
fi

compose up -d --scale api=2
wait_ready() {
    curl --silent --show-error --fail --retry 15 --retry-delay 1 --retry-all-errors \
        --cacert "$TLS_DIRECTORY/cert.pem" "$PUBLIC_ORIGIN/health/ready"
    printf '\n'
}
wait_ready
# A peer outside the trusted proxy cannot make direct cleartext appear as HTTPS.
compose exec -T database bash -e <<'FORWARDED'
exec 3<>/dev/tcp/api/8080
printf 'GET /health/live HTTP/1.1\r\nHost: localhost\r\nX-Forwarded-Proto: https\r\nConnection: close\r\n\r\n' >&3
IFS= read -r line <&3
[[ $line == "HTTP/1.1 400 "* ]]
FORWARDED
if [[ -n ${PREVIOUS_IMAGE:-} ]]; then
    old_note="$(compose exec -T database psql -U sb_bootstrap -d superbackend -Atc \
        "SELECT count(*) FROM notes.\"Notes\" WHERE \"Title\"='Previous image fixture'")"
    [[ $old_note == 1 ]]
fi
export Bootstrap__Password="$REHEARSAL_PASSWORD"
compose run --rm --no-deps -e Bootstrap__Password migrate --bootstrap-admin operator@example.test
unset Bootstrap__Password
python3 deploy/tests/reference.py enroll

if [[ -n "${PLAYWRIGHT_DIRECTORY:-}" ]]; then
    compose exec -T database psql -v ON_ERROR_STOP=1 -U sb_bootstrap -d superbackend <<'SQL'
INSERT INTO identity."AspNetUsers"
    ("Id", "Email", "NormalizedEmail", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp",
     "ConcurrencyStamp", "EmailConfirmed", "IsDisabled", "LockoutEnabled", "PhoneNumberConfirmed",
     "TwoFactorEnabled", "AccessFailedCount")
SELECT gen_random_uuid(), 'member@example.test', 'MEMBER@EXAMPLE.TEST', 'member@example.test',
       'MEMBER@EXAMPLE.TEST', "PasswordHash", gen_random_uuid(), gen_random_uuid(), true, false, true, false, false, 0
FROM identity."AspNetUsers" WHERE "Email" = 'operator@example.test';
SQL
    cp deploy/tests/admin-browser.mjs "$PLAYWRIGHT_DIRECTORY/managed-admin-browser.mjs"
    node "$PLAYWRIGHT_DIRECTORY/managed-admin-browser.mjs"
fi

if compose exec -T -e PGPASSWORD="$DB_RUNTIME_PASSWORD" database psql -h 127.0.0.1 \
      -U sb_runtime -d superbackend -v ON_ERROR_STOP=1 -c 'CREATE SCHEMA runtime_should_not_create' \
      >"$scratch/ddl.log" 2>&1; then
    echo "The runtime role unexpectedly has DDL permissions." >&2
    exit 1
fi
grep -qi 'permission denied' "$scratch/ddl.log"
if compose exec -T -e PGPASSWORD="$DB_RUNTIME_PASSWORD" database psql -h 127.0.0.1 \
      -U sb_runtime -d superbackend -v ON_ERROR_STOP=1 \
      -c 'DELETE FROM identity."AccessAudits" WHERE false' >"$scratch/audit-grant.log" 2>&1; then
    echo "The runtime role unexpectedly has audit deletion permissions." >&2
    exit 1
fi
grep -qi 'permission denied' "$scratch/audit-grant.log"
compose up -d --no-deps --force-recreate --scale api=2 api
wait_ready
python3 deploy/tests/reference.py replica

if [[ ${REHEARSAL_LOAD:-true} == true ]]; then
    started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    python3 deploy/tests/reference.py workload &
    workload_pid=$!
    peak_connections=0
    while kill -0 "$workload_pid" 2>/dev/null; do
        connections="$(compose exec -T database psql -U sb_bootstrap -d superbackend -Atc             "SELECT count(*) FROM pg_stat_activity WHERE usename = 'sb_runtime' AND state = 'active'")"
        if (( connections > peak_connections )); then peak_connections="$connections"; fi
        sleep 0.2
    done
    wait "$workload_pid"
    traffic_replicas="$(compose logs --since "$started_at" --no-color --no-log-prefix ingress |
        awk '$2 == "POST" && $3 == "/api/v1/notes" && $4 == "201" { print $6 }' | sort -u | wc -l)"
    [[ $traffic_replicas == 2 ]]
    export peak_connections traffic_replicas
    started=$SECONDS
    job_ids="$(python3 -c 'import json,sys; print(",".join(json.load(open(sys.argv[1]))))' "$scratch/jobs.json")"
    for attempt in {1..30}; do
        drained="$(compose exec -T database psql -U sb_bootstrap -d superbackend -At -v job_ids="$job_ids" <<'SQL'
SELECT count(*) FROM jobs."Jobs"
WHERE "Id" = ANY(string_to_array(:'job_ids', ',')::uuid[]) AND "Status" = 'succeeded';
SQL
)"
        [[ $drained == "${REHEARSAL_NOTE_SAMPLES:-12}" ]] && break
        sleep 1
    done
    [[ $drained == "${REHEARSAL_NOTE_SAMPLES:-12}" ]]
    export drain_seconds="$((SECONDS - started))"
fi

if [[ ${REHEARSAL_POOL_DRILL:-false} == true ]]; then
    # Wait for the IP burst budget to refill before deliberately holding a database lock.
    sleep 3
    compose exec -T -e PGAPPNAME=sb_rehearsal_lock database psql -U sb_bootstrap -d superbackend         -v ON_ERROR_STOP=1 >"$scratch/pool-lock.log" <<'SQL' &
BEGIN;
LOCK TABLE notes."NoteCreateReceipts" IN ACCESS EXCLUSIVE MODE;
SELECT pg_sleep(5);
ROLLBACK;
SQL
    lock_pid=$!
    for attempt in {1..20}; do
        lock_active="$(compose exec -T database psql -U sb_bootstrap -d superbackend -Atc             "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name='sb_rehearsal_lock' AND wait_event='PgSleep')")"
        [[ $lock_active == t ]] && break
        sleep 0.1
    done
    [[ $lock_active == t ]]
    python3 deploy/tests/reference.py pool &
    load_pid=$!
    peak_pool_wait=0
    while kill -0 "$load_pid" 2>/dev/null; do
        blocked="$(compose exec -T database psql -U sb_bootstrap -d superbackend -Atc             "SELECT coalesce(max(blocked), 0) FROM (SELECT count(*) AS blocked FROM pg_stat_activity
            WHERE usename='sb_runtime' AND wait_event_type='Lock' GROUP BY client_addr) AS by_replica")"
        if (( blocked > peak_pool_wait )); then peak_pool_wait="$blocked"; fi
        sleep 0.2
    done
    wait "$lock_pid"
    wait "$load_pid"
    [[ $peak_pool_wait == 10 ]]
    export peak_pool_wait
fi

compose stop database
for attempt in {1..12}; do
    code="$(curl --silent --cacert "$TLS_DIRECTORY/cert.pem" -o /dev/null -w '%{http_code}' "$PUBLIC_ORIGIN/health/ready")"
    [[ $code == 503 ]] && break
    sleep 1
done
[[ $code == 503 ]]
curl --silent --show-error --fail --cacert "$TLS_DIRECTORY/cert.pem" "$PUBLIC_ORIGIN/health/live"
compose up -d --wait database
wait_ready

compose exec -T database psql -v ON_ERROR_STOP=1 -U sb_bootstrap -d superbackend <<'SQL'
INSERT INTO jobs."Jobs" ("Id", "Type", "Payload", "Status", "Attempts", "MaxAttempts", "AvailableAt", "FinishedAt")
VALUES ('03000000-0000-0000-0000-000000000001', 'fixture.settled', '{}', 'succeeded', 1, 5, now() - interval '40 days', now() - interval '40 days'),
       ('03000000-0000-0000-0000-000000000002', 'fixture.pending', '{}', 'pending', 0, 5, now() - interval '40 days', NULL);
INSERT INTO outbox."Messages" ("Id", "Type", "Payload", "MaxAttempts", "CreatedAt", "DispatchedAt")
VALUES ('03000000-0000-0000-0000-000000000001', 'fixture.settled', '{}', 5, now() - interval '40 days', now() - interval '40 days');
INSERT INTO outbox."InboxReceipts" ("MessageId", "Consumer", "ProcessedAt")
VALUES ('03000000-0000-0000-0000-000000000001', 'fixture.settled', now() - interval '40 days');
SQL
compose run --rm --no-deps api --maintenance
compose run --rm --no-deps api --maintenance
retained="$(compose exec -T database psql -U sb_bootstrap -d superbackend -At <<'SQL'
SELECT
    EXISTS (SELECT 1 FROM jobs."Jobs" WHERE "Type" = 'fixture.pending') AND
    NOT EXISTS (SELECT 1 FROM jobs."Jobs" WHERE "Type" = 'fixture.settled') AND
    NOT EXISTS (SELECT 1 FROM outbox."Messages" WHERE "Type" = 'fixture.settled') AND
    NOT EXISTS (SELECT 1 FROM outbox."InboxReceipts" WHERE "Consumer" = 'fixture.settled');
SQL
)"
[[ $retained == t ]]

history_rows="${REHEARSAL_HISTORY_ROWS:-25000}"
[[ $history_rows =~ ^[1-9][0-9]{0,5}$ ]]
compose exec -T database psql -U sb_bootstrap -d superbackend -At -v ON_ERROR_STOP=1 -v rows="$history_rows"     <deploy/tests/history.sql >"$scratch/history.json"
export history_rows

note_id="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["note"])' "$scratch/private-session.json")"
compose exec -T database pg_dump -U sb_bootstrap -Fc superbackend >"$scratch/rehearsal.dump"
restore_started="$(date +%s%3N)"
compose exec -T database createdb -U sb_bootstrap rehearsal_restore
compose exec -T database pg_restore -U sb_bootstrap --exit-on-error --no-owner --no-privileges \
    --dbname=rehearsal_restore <"$scratch/rehearsal.dump"
restore_millis="$(( $(date +%s%3N) - restore_started ))"
restored="$(compose exec -T database psql -U sb_bootstrap -d rehearsal_restore -At \
    -v note_id="$note_id" <<'SQL'
SELECT count(*) FROM notes."Notes" WHERE "Id" = :'note_id';
SQL
)"
test "$restored" = 1
[[ -z "${MAX_RESTORE_SECONDS:-}" || $restore_millis -le $(( MAX_RESTORE_SECONDS * 1000 )) ]]

export restore_millis
python3 - <<'PY'
import json, os, subprocess
from pathlib import Path
image = os.environ["API_IMAGE"]
digest = subprocess.check_output(["docker", "image", "inspect", "--format", "{{.Id}}", image], text=True).strip()
entry = {"image": digest, "replicas": 2, "restoreSeconds": int(os.environ["restore_millis"]) / 1000,
         "committedIndicatorRecovered": True, "databaseOutageDetected": True, "linkedRetentionVerified": True}
if previous := os.getenv("PREVIOUS_IMAGE"):
    entry["previousImage"] = subprocess.check_output(
        ["docker", "image", "inspect", "--format", "{{.Id}}", previous], text=True).strip()
    entry["previousSchemaRecordPreserved"] = True
if "drain_seconds" in os.environ:
    entry.update(json.loads((Path(os.environ["EVIDENCE_DIRECTORY"]) / "workload.json").read_text()))
    entry["queueDrainSecondsAfterBurst"] = int(os.environ["drain_seconds"])
    entry["peakSampledActiveRuntimeConnections"] = int(os.environ["peak_connections"])
    entry["replicasServingNotes"] = int(os.environ["traffic_replicas"])
    entry["observedSettledJobsPerSecond"] = round(entry["noteSamples"] /
        (entry["noteProducerSeconds"] + entry["queueDrainSecondsAfterBurst"]), 2)
if "peak_pool_wait" in os.environ:
    entry["blockedConnectionsAtReplicaPoolLimit"] = int(os.environ["peak_pool_wait"])
plan, = json.loads((Path(os.environ["EVIDENCE_DIRECTORY"]) / "history.json").read_text())
entry["historyRows"] = int(os.environ["history_rows"])
entry["leaseCandidateExecutionMs"] = plan["Execution Time"]
if cutoff := os.getenv("MAX_LEASE_CANDIDATE_MS"):
    assert plan["Execution Time"] <= float(cutoff), plan["Execution Time"]
print(json.dumps(entry))
if output := os.getenv("REHEARSAL_EVIDENCE_FILE"):
    path = Path(output)
    path.with_suffix(".history.json").write_text(json.dumps(plan, indent=2) + "\n")
    with open(path, "w") as file:
        json.dump(entry, file, indent=2)
        file.write("\n")
PY
