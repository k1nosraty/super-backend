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

The optional modules add OIDC sign-in, tenant isolation and a PostgreSQL job queue. See [optional-module configuration](OPTIONAL-MODULES.md). The [Transactional Outbox](TRANSACTIONAL-OUTBOX.md) can be enabled for atomic database-backed job recording. The SMTP sender currently dispatches inline. Production load targets, RPO/RTO, retention, and incident ownership depend on the deployment.

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

Terminate TLS at the host or a configured trusted proxy. Run health checks over HTTPS outside Development. `/health/live` checks the process and `/health/ready` checks connections for the enabled modules. Verify migration completion independently. Restrict the administration panel to the intended network where possible and enable TOTP for administrative users.

Outside Development, the application emits JSON console logs with trace and span scopes. Problem Details responses include a trace ID. With OTLP enabled, incoming HTTP traces and HTTP/runtime metrics are exported. Query strings are excluded from trace attributes. Configure ingress and application log sinks to avoid recording credentials, bearer tokens, or confirmation URL query parameters. Keep audit access and retention aligned with the deployment's operating requirements.

Further reading: [PostgreSQL connection encryption](https://www.npgsql.org/doc/security.html) and [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/).
