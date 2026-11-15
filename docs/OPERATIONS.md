# Operations

## Backend lifecycle coverage

| Phase | Included |
| --- | --- |
| Bootstrap and development | Named .NET solution template, loopback Compose stack, local SMTP inbox, locked packages, and directory conventions |
| API design and implementation | Versioned routes, development OpenAPI, problem details, input validation, localization, and an optional reference module |
| Identity and application security | Local account enrollment, email and TOTP verification, session expiry and revocation, permissions, admin UI, audit, TLS enforcement, and explicit trusted proxies |
| Schema evolution | Independent PostgreSQL schemas, source-controlled EF migrations, idempotent review SQL, drift checks, and a one-shot migration command |
| Verification and delivery | Release build, integration suite with real PostgreSQL and Mailpit, and GitHub Actions |
| Observation and recovery | Probe endpoints, correlated JSON logs, opt-in OTLP metrics/traces, and a development backup/restore rehearsal |

The optional modules add OIDC sign-in, tenant isolation and a PostgreSQL job queue. See [optional-module configuration](OPTIONAL-MODULES.md). The [Transactional Outbox](TRANSACTIONAL-OUTBOX.md) can be enabled for atomic database-backed job recording. The SMTP sender supports inline delivery and an optional durable delivery profile. Production load targets, RPO/RTO, retention, and incident ownership depend on the deployment.

## Database commands

```bash
./scripts/db.sh check Identity
./scripts/db.sh check Notes
./scripts/db.sh check Tenancy
./scripts/db.sh check Jobs
./scripts/db.sh check Outbox
./scripts/db.sh add Notes AddArchiveState
./scripts/db.sh sql Identity ./identity-migrations.sql
./scripts/db.sh apply
./scripts/db.sh backup ./development.dump
./scripts/db.sh restore ./development.dump recovery_check
```

The `add`, `sql`, and `check` commands accept an optional DbContext name. The default is the module name followed by `Data`. `restore` creates a new database and fails if that name already exists. `backup` uses the development Compose database and creates a private file without overwriting an existing one. Restored data may contain credentials and personal information. Keep PostgreSQL backups and Data Protection key backups in access-controlled storage, and verify restores before cutting over. In production, use the database service's backup and recovery controls.

## Production settings

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Main` | PostgreSQL secret. Use `SSL Mode=VerifyFull` and the proper CA chain when connecting over a network. |
| `AllowedHosts` | Semicolon-separated host names accepted by the API. |
| `Identity__PublicOrigin` | Canonical HTTPS origin used in confirmation emails. |
| `Identity__AllowRegistration` | Enable public registration. Defaults to false outside Development. |
| `Modules__AdminPanel` / `Modules__Notes` | Enable the optional browser panel or sample module. Defaults to false outside Development. |
| `Smtp__Host`, `Smtp__Port`, `Smtp__From` | SMTP relay using STARTTLS. The default SMTP port is 587. |
| `Smtp__Username`, `Smtp__Password` | Relay credentials if required. |
| `DataProtection__KeyDirectory` | Persistent private key ring shared by replicas within one environment. Encrypt and back up the underlying storage. |
| `Http__KnownProxies__0` | Trusted proxy IP for forwarded IP and protocol. Add indexed entries for additional IPs. |
| `Http__CorsOrigins__0` | Exact allowed HTTPS browser origin. Empty disables cross-origin browser access. |
| `RateLimits__RequestsPerMinute`, `RateLimits__IdentityRequestsPerMinute` | In-process per-IP request limits. Apply shared limits upstream for a multi-instance deployment. |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` | Enable trace and metric export. For an HTTP collector, use the collector base URL and `http/protobuf`. |
| `OTEL_SERVICE_NAME` | Override the service name used by the collector. |

Run the same release image once with `--migrate` before serving traffic. Use a dedicated DDL credential for migration and a narrower credential for runtime. Review the generated SQL and backups before data-changing releases. Choose forward-compatible changes when old and new application instances share a database.

Terminate TLS at the host or a configured trusted proxy. Run health checks over HTTPS outside Development. `/health/live` checks the process and `/health/ready` checks that the expected EF migrations are present for every enabled schema. Restrict the administration panel to the intended network where possible and require MFA for privileged sessions.

Outside Development, the application emits JSON console logs with trace and span scopes. Problem Details responses include a trace ID. With OTLP enabled, incoming HTTP traces and HTTP/runtime metrics are exported. Query strings are excluded from trace attributes. Configure ingress and application log sinks to avoid recording credentials, bearer tokens, or confirmation URL query parameters. Keep audit access and retention aligned with the deployment's operating requirements.

Further reading: [PostgreSQL connection encryption](https://www.npgsql.org/doc/security.html) and [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/).

## Reference deployment

Use [the reference Compose profile](../deploy/reference/compose.yaml) for a single-host, two-replica topology. The TLS proxy is the only published service. It replaces forwarded client IP and scheme headers and uses a fixed trusted backend address. It applies a shared per-source-IP budget of 20 requests/second with a burst of 40, and 3 requests/second with a burst of 12 for authentication and OIDC. Clients behind the same NAT share that budget. The app's per-replica defaults are 180 requests/minute and 20 authentication requests/minute per IP. The health endpoints bypass the app limiter. Configure each limit and its burst for the target topology. Clients should retry a 429 with backoff and use `Retry-After` when present. The ingress caps bodies at 32 KiB.

Run the deterministic fixture with `./scripts/rehearse-release.sh`. It creates isolated Compose state and local TLS credentials, checks migrations and runtime DDL denial, replaces replicas, runs a short load fixture and an outage drill, and restores a committed record into a separate database. Set `PREVIOUS_IMAGE` to a local previous image for upgrade rehearsal. Set `REHEARSAL_EVIDENCE_FILE` to capture the sanitized results and `PLAYWRIGHT_DIRECTORY` to enable the real browser journey. For a paced fixture, set `REHEARSAL_NOTE_SAMPLES=120 REHEARSAL_NOTE_RATE=10`. The output includes sampled active PostgreSQL connections, job throughput and a synthetic history execution plan. Set `REHEARSAL_HISTORY_ROWS` for the fixture size and `MAX_P95_SECONDS`, `MAX_LEASE_CANDIDATE_MS` or `MAX_RESTORE_SECONDS` for deployment-approved budgets. Set `REHEARSAL_POOL_DRILL=true` to hold a fixture table lock and verify that blocked requests reach the configured pool limit, then recover when the lock is released. Measure pool pressure and queue latency with the intended workload in the target topology. [Npgsql performance diagnostics](https://www.npgsql.org/doc/performance.html)

The browser uses a locally generated test certificate. The HTTP and recovery checks verify the CA explicitly.

For a real deployment, supply the API image by registry digest and configure the secrets, connection strings, public endpoint, TLS certificate, module flags, and provider settings referenced by the Compose file. Provision the key ring on an encrypted volume writable by container UID 1654. Pin database and API artifacts, choose backup encryption and an access-controlled backup destination, and keep the key-ring backup separately with tested restore permissions. Prefer the PostgreSQL service's tested point-in-time recovery for production. Before cutover, restore a representative backup into a separate environment, compare recovery point and time to the deployment objectives, verify that the application reads the restored record with runtime credentials, and validate the key ring using a pre-restore session with a valid remaining lifetime. Assign sign-off to the service and database owners.

Use the API binary with `--check-profile` to validate startup configuration without listening or migrating. Apply with `--migrate` under the migrator credential. Install [table grants](../deploy/reference/grants.sql) before admitting traffic. The runtime identity can append access audit events and read them through authorization-protected routes. The tenant document role uses a separate constrained connection and is subject to PostgreSQL row-level security.

## Privileged account recovery

Production privileged actions require a session with TOTP assurance. Bootstrap a named operator with `--bootstrap-admin EMAIL` and a single-use injected `Bootstrap__Password`, then enroll TOTP with that account. Keep recovery codes offline and provision a second named operator before broad use. To revoke, suspend, or reset an administrator, run:

```bash
# Inject Recovery__Operator and Recovery__Reason through the job's environment.
dotnet SuperBackend.Api.dll --recover-admin operator@example.test revoke-sessions
dotnet SuperBackend.Api.dll --recover-admin operator@example.test suspend
# Supplied through secret storage for this action only: Recovery__Password.
dotnet SuperBackend.Api.dll --recover-admin operator@example.test reset-mfa
```

Authorize the operation with restricted deployment and database permissions. The process stores the named operator, target, reason, timestamp, and a correlation ID. MFA reset changes the password, retires existing MFA and sessions, and requires the operator to enroll again. Suspension guards the last usable administrator and checks that another enrolled administrator remains before suspending an enrolled operator where MFA is required. Rotate ephemeral credentials after recovery and check the audit trail.

Sessions have ten-minute access credentials and a seven-day absolute session deadline. Refresh rotates on a single atomic generation update. A replay, or a second caller refreshing the same token concurrently, receives 401. Coordinate refresh across tabs or workers. `POST /api/v1/auth/logout` revokes the current local session. Password changes, support revocation, suspension and role changes invalidate sessions through the account security stamp. Deployments upgrading from the earlier bearer-only implementation should schedule user sign-in again, because existing tokens have no persisted session ID.

## Diagnostics and retention

Export the `SuperBackend.Operations` meter through OTLP. Build dashboards for `superbackend.operation.outcomes` and `superbackend.operation.duration` using the bounded component/outcome attributes configured in the application. Alert on prolonged readiness failure, SMTP delivery errors, dead jobs, pending work age, and outbox conflicts. Inspect backlog with the restricted queue and outbox APIs, or query aggregate pending counts and oldest timestamps from the reporting channel. To follow a transactional event, use the note ID as the outbox/job correlation ID, then follow any explicit replacement ID. The private operator API supplies metadata. It omits payloads, recipients, reset codes, and tokens.

Exercise each alert with a real stopped database, an unavailable test SMTP relay, an API profile with `Outbox__RunRelay=false`, and a deliberately failing test job. Verify that the alert identifies the affected service, that readiness is restored after the database returns, and that queued delivery or processing resumes after restoring a capable worker. Record the observed time and alert destination in the release evidence for the deployment.

Schedule the API binary with `--maintenance` using the runtime role, exact target feature profile, and private runner configuration. `Retention__FinishedDays` (default 30) defines both the linked delivery and Notes idempotency retention window. `Retention__BatchSize` defaults to 1000. Each execution uses one transaction and bounded batches. Expired auth sessions and OIDC handoffs are removed. Settled outbox/job/inbox rows age out together. Pending, running, dead, or conflicted work and standalone job IDs need a deployment-owned disposition policy. Re-run after interruption. Clients should stop retrying Notes requests after the documented window. Keep backups and protected audit archives under the deployment's privacy and retention policy.

For offboarding, suspend the account or tenant first, confirm sessions and membership access are denied, then export or erase product data according to the organization's retention policy. Revoke OIDC links and confirm local access immediately. A tenant suspension freezes new authorizations and preserves tenant records. Let already authorized requests and background transactions finish safely. Verify an export or erasure on representative data and keep an auditable record of the decision. The starter exposes lifecycle controls and history protection. Export and deletion of product-owned records use that product's data model and policies.
