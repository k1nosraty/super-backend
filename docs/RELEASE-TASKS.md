# V1 release tasks

This backlog transcribes all 22 findings from the review of commit `9c587e2`. Use the numbered headings as issue titles and keep their acceptance criteria when splitting implementation. See [implementation evidence](RELEASE-EVIDENCE.md) for verification and external release gates.

## Milestone 1 — Core production gates

These items must close before the starter is advertised for production use.

### R01 · P0 · Make readiness validate database schemas

**Evidence.** The Identity, Notes, and Jobs registrations use connection-oriented checks. The fresh-database probe returned `200 Healthy` with no schema tables. Outbox checks selected relation names without verifying the expected migration level. [IdentityModule.cs](../src/Modules/SuperBackend.Identity/IdentityModule.cs#L81) · [OutboxModule.cs](../src/Modules/SuperBackend.Outbox/OutboxModule.cs#L20) · [Program.cs](../src/SuperBackend.Api/Program.cs#L128)

**Task.** Define a lightweight, bounded readiness check for the minimum compatible migration or schema version of every enabled module. Keep liveness independent from PostgreSQL. Decide how a compatible newer schema behaves during a rolling deployment.

**Acceptance.** Empty, partially migrated, and unavailable databases return ready `503`. A migrated database and a deliberately compatible newer schema return `200`. Liveness remains `200` during a database outage. CI exercises disabled features, baseline, and all supported features.

### R02 · P0 · Require privileged MFA and provide audited operator recovery

**Evidence.** Admin access is granted through the built-in Administrator role. Two-factor enrollment is available, the setup command creates an admin with a password, and the account action endpoint refuses both suspend and revoke for protected administrators. The CLI currently exposes bootstrap and migration commands. [IdentityModule.cs](../src/Modules/SuperBackend.Identity/IdentityModule.cs#L178) · [AdminUserEndpoints.cs](../src/Modules/SuperBackend.Identity/Administration/AdminUserEndpoints.cs#L45) · [AccessPolicies.cs](../src/Modules/SuperBackend.Identity/Administration/AccessPolicies.cs#L15)

**Task.** Require verified MFA for privileged operations, define safe first enrollment, and add a controlled operator recovery procedure that can revoke a compromised admin's sessions and suspend or replace the account. Audit the named operator and prevent accidental loss of the last usable administrator. Define how existing administrators enroll without being locked out.

**Acceptance.** A password-only admin session cannot use privileged operations. An MFA-authenticated admin can. Recovery invalidates existing access and refresh credentials and produces an audit event. Bootstrap, emergency recovery, lost MFA device, and last-operator safeguards are exercised.

### R03 · P0 · Set a revocable session contract

**Evidence.** The host builds on ASP.NET Core Identity's proprietary bearer tokens. Refresh lifetime is seven days. There is account-wide stamp revocation, and the admin console clears its local session on sign-out. The implementation has no persistent per-session records or refresh-family replay detection. [IdentityModule.cs](../src/Modules/SuperBackend.Identity/IdentityModule.cs#L46) · [IdentityModule.cs](../src/Modules/SuperBackend.Identity/IdentityModule.cs#L142) · [panel.js](../src/SuperBackend.Api/wwwroot/admin/panel.js#L116) · [Microsoft Identity bearer token guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0)

**Task.** Define session duration, single-session logout, administrator revocation, refresh rotation and replay behavior. Use a supported solution. A first-party server-side session or a suitable identity provider may reduce the need for application-owned token protocol code.

**Acceptance.** A revoked session cannot refresh. Concurrent refresh behavior is deterministic. Previously consumed refresh credentials follow the documented replay response. Other sessions remain active after single-session logout. Suspension, password change, and account-wide logout retain their documented revocation semantics across replicas and restarts.

### R04 · P0 · Ship one reproducible production deployment and recovery profile

**Evidence.** Compose is deliberately development-only and uses Mailpit and the PostgreSQL bootstrap account. The Docker image runs non-root. Operations notes describe TLS, trusted proxies, key-directory storage, and dedicated database credentials. There is no deployable staging reference with those controls verified together. [compose.yaml](../compose.yaml#L22) · [Dockerfile](../Dockerfile#L8) · [ApiSecurity.cs](../src/SuperBackend.Api/Infrastructure/ApiSecurity.cs#L25) · [OPERATIONS.md](../docs/OPERATIONS.md#L33)

**Task.** Document and automate one supported pattern with TLS and trusted ingress, runtime/migrator DB roles, key-ring sharing and protection, injected secrets, backup/recovery for the database and keys, and predictable rollout. Require staged compatibility for old/new app versions, an explicit migration job, and a forward-fix or rollback procedure. Use the deployment platform's secret/key services where available.

**Acceptance.** From a clean environment, deploy an immutable image, migrate with the migrator principal, serve traffic with a role that cannot modify schemas, replace a replica without invalidating sessions, and restore in an isolated environment. Record recovery time and recovered point against approved RTO/RPO. Smoke checks cover HTTPS, verified forwarded headers, auth, and readiness. Sample credentials are rejected or structurally excluded from the production profile.

## Milestone 2 — Production reliability and contract

### R05 · P1 · Make the generated OpenAPI accurate and stable

**Evidence.** Untyped Minimal API results and unannotated MVC actions produce missing or incorrect response contracts. The generated Notes and Jobs contracts demonstrably differ from runtime. OIDC exchange requires a short-lived cookie and an exact Origin, while the generated operation has no such security contract. [NotesModule.cs](../src/Modules/SuperBackend.Notes/NotesModule.cs#L64) · [JobsController.cs](../src/Modules/SuperBackend.Jobs/Controllers/JobsController.cs#L15) · [ApiOpenApi.cs](../src/SuperBackend.Api/Infrastructure/ApiOpenApi.cs#L22)

**Task.** Document success and error statuses, response schemas, relevant headers, authentication schemes, job retry semantics, and optional routes. Add a CI contract check and a version/deprecation policy. Publish an approved artifact with each release. The API need not serve a public Swagger UI.

**Acceptance.** CI compares key routes with runtime examples in base/all-feature builds. Note creation has a documented `201` and body. Queue retry advertises `204` and `409`. Error bodies have a stable programmatic field or type across languages. Breaking v1 changes require an explicit migration and version decision.

### R06 · P1 · Harden framework endpoint integration and validation

**Evidence.** `MapIdentityApi` is decorated by route path, all framework routes outside `manage` are marked anonymous, and protection wrappers depend on exact framework paths. Minimal APIs use a custom validation filter while MVC uses its own validation pipeline. [IdentityModule.cs](../src/Modules/SuperBackend.Identity/IdentityModule.cs#L87) · [RequestValidation.cs](../src/SuperBackend.Platform/RequestValidation.cs#L7)

**Task.** Make the auth exposure policy explicit and fail closed on unfamiliar routes. Regression-test intended anonymity, password step-up, and cookie mode against framework upgrades. Define uniform validation and Problem Details behavior for the APIs consumers use. Keep ASP.NET Core primitives where they fit.

**Acceptance.** CI fails when framework auth routes change without a review of policy, request format, and limits. Cases cover malformed JSON, nulls, overlong values, invalid enums, unsupported media types, and password step-up. MFA and reset behavior continue to pass.

### R07 · P1 · Put delivery and lifecycle around identity mail

**Evidence.** Invite persistence commits before synchronous SMTP. SMTP executes inline with a 15-second timeout. A provider outage can leave committed data and a failed delivery, with operator resubmission as recovery. [AccountInvitations.cs](../src/Modules/SuperBackend.Identity/Accounts/AccountInvitations.cs#L26) · [SmtpEmailSender.cs](../src/Modules/SuperBackend.Identity/Mail/SmtpEmailSender.cs#L48)

**Task.** For each supported email flow, define durable delivery or explicit recoverable resend behavior. Reuse Jobs/Outbox when durable delivery is required. Queue a safe intent or protected payload. Keep plaintext verification tokens and passwords out of job history and logs. Handle email and confirmation flow privacy with generic client-facing responses where appropriate.

**Acceptance.** SMTP outage, retry, delayed delivery, expired code, and resume are tested. Accepted invitations can be recovered without orphaning the account or conflicting with valid retry behavior. Pending or failed delivery is observable. No secret message contents are captured by operational logging.

### R08 · P1 · Define HTTP idempotency for operations subject to retries

**Evidence.** Job and outbox submissions deduplicate by stable ID. HTTP note creation allocates a new note ID for each request, and tenant creation also uses a new GUID. An ambiguous network failure after commit therefore remains an application-level retry problem. [NotesModule.cs](../src/Modules/SuperBackend.Notes/NotesModule.cs#L67) · [TenantDirectory.cs](../src/Modules/SuperBackend.Tenancy/Services/TenantDirectory.cs#L19)

**Task.** Support an explicit idempotency contract on create commands that clients are expected to retry. Scope keys to principal or tenant and operation. Persist request/result metadata with business state and outbox entries. Decide which routes need it and their retention window. Make mutation concurrency consistent where two editors can overwrite each other.

**Acceptance.** Retried and concurrent requests with the same key/payload yield one business write and one job. A conflicting reuse is rejected and keys cannot reveal another actor's result. Tenant document updates have a documented concurrency contract and regression tests.

### R09 · P1 · Implement audit coverage and data lifecycle rules

**Evidence.** Identity administration already writes transactional audit records. Tenant creation and membership mutations and job redrive lack the same operator audit trail. Jobs, outbox messages, inbox receipts, Notes statistics, and identity/tenant data have no executable retention or offboarding policy in the repository. [AdminEndpoints.cs](../src/Modules/SuperBackend.Identity/Administration/AdminEndpoints.cs#L208) · [TenantDirectory.cs](../src/Modules/SuperBackend.Tenancy/Services/TenantDirectory.cs#L27) · [JobsController.cs](../src/Modules/SuperBackend.Jobs/Controllers/JobsController.cs#L15)

**Task.** Specify audit event coverage, reader/writer grants, retention, and correlation. Specify account and tenant export, deletion or anonymization, and how backup expiry interacts with those policies. Keep job/inbox dedupe windows consistent. Avoid copying payloads or bearer material into audit metadata.

**Acceptance.** Tests capture actor, target, reason, timestamp, and correlation for privileged changes. Unauthorized roles cannot read or alter protected audit records. Scheduled or documented batch cleanup leaves pending work and deduplication guarantees intact. Offboarding can be exercised on a representative data set.

### R10 · P1 · Measure meaningful service and queue health

**Evidence.** OpenTelemetry currently covers inbound HTTP and runtime metrics. Worker failure paths primarily log warnings. There are no work-age, lease-expiry, dispatch-conflict, dead-letter, or handler-duration instruments in the repository. [Observability.cs](../src/SuperBackend.Api/Infrastructure/Observability.cs#L26) · [JobWorker.cs](../src/Modules/SuperBackend.Jobs/Services/JobWorker.cs#L18) · [OutboxWorker.cs](../src/Modules/SuperBackend.Outbox/Services/OutboxWorker.cs#L16)

**Task.** Set baseline SLOs and add bounded-cardinality metrics for dependency availability, queue lag, retries, dead work, and outbox conflicts. Add alerts for lost workers/relay, a sustained backlog, and broken delivery. Define correlation through business write, outbox, job, and handler. Preserve sensitive data redaction at ingress, application, and telemetry exporters.

**Acceptance.** Staged database outage, SMTP outage, stopped relay, and dead-job drills trigger the expected alert or diagnostic. Operators can follow one business event to its eventual job result. IDs, email, payload contents, and secrets are excluded from metric labels and protected diagnostic paths.

### R11 · P1 · Establish limits and a measured performance envelope

**Evidence.** The app uses per-IP fixed-window counters in each instance, a body-size limit, and several bounded page sizes. The repository has no named load target, response-time objective, or recorded exhaustion test. [ApiSecurity.cs](../src/SuperBackend.Api/Infrastructure/ApiSecurity.cs#L46) · [Program.cs](../src/SuperBackend.Api/Program.cs#L20)

**Task.** Agree expected traffic, tenant count, concurrency, backlog size, and p95/p99 thresholds. Benchmark the core API, a tenant query, and a producer/worker workload against the chosen PostgreSQL profile. Place shared abuse limits at the ingress for multi-instance deployment. Add account-sensitive and tenant-sensitive budgets only where incidents or measurement justify them.

**Acceptance.** A repeatable test establishes latency, connection-pool saturation, and sustained job throughput. Rate limiting behaves as intended across two replicas, behind the actual proxy topology, and for shared NAT traffic. Rate and request budget behavior is documented. [OWASP API resource consumption guidance](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/)

### R12 · P1 · Publish repeatable release evidence and dependency updates

**Evidence.** GitHub Actions builds and tests the solution, verifies migration drift, builds a generated solution and container, and pins the SDK version. The workflow does not exercise a production-like deployment, publish an immutable release artifact, or show a scheduled dependency/image update and scanning workflow. [ci.yml](../.github/workflows/ci.yml#L9) · [Dockerfile](../Dockerfile#L1)

**Task.** Add a release workflow with immutable image references, actionable dependency and image scanning, tracked update cadence, an SBOM where required, and deployment smoke evidence. Keep the ordinary PR pipeline fast. Split slow fault/recovery and load checks into a scheduled or release job.

**Acceptance.** CI builds on a clean runner, verifies a representative generated solution, tests both fresh migration and an upgrade from the previous released schema, and records the image digest, tests, and scan policy result. Runtime and base-image patch updates have an owner and a reproducible process.

### R13 · P1 · Reject invalid project names before generation

**Evidence.** `new-project.sh` accepted `class`; build then failed with CS1001. The shell regex checks spelling but not C# namespace validity or the name transformations across the solution, Compose, PostgreSQL, and resource files. [new-project.sh](../scripts/new-project.sh#L4) · [template.json](../.template.config/template.json#L8)

**Task.** Define and test supported names and size bounds. Safely derive source and infrastructure names and fail cleanly before writing invalid outputs. Verify that both name-based project scaffolding and incremental service scaffolding produce the documented wiring.

**Acceptance.** Reserved C# names and incompatible segments are rejected without partial files. At least two nontrivial valid project names pass locked restore, Release build, migration, and HTTP smoke tests. Existing paths remain protected, and interrupted generation has an explicit recovery path.

## Milestone 3 — Optional profile gates

These items are required before marking the corresponding optional module production-supported. A v1 profile that leaves a feature off can defer its gate while publishing the supported configuration accurately.

### R14 · P1 · Enforce a one-time OIDC handoff and define provider deprovisioning

**Evidence.** OIDC uses authorization code with PKCE and exact issuer/subject linking. The exchange validates Origin and local eligibility, then clears a short-lived cookie. The server has no atomic consumed-ticket record, and upstream sign-out or deprovisioning does not automatically revoke local bearer sessions. [OrganizationSignInController.cs](../src/Modules/SuperBackend.Federation/Controllers/OrganizationSignInController.cs#L29) · [FederationModule.cs](../src/Modules/SuperBackend.Federation/FederationModule.cs#L57) · [OPTIONAL-MODULES.md](../docs/OPTIONAL-MODULES.md#L27)

**Task.** Consume handoffs once across replicas, decide local session treatment when upstream authorization changes, and document the supported client origin and provider policy. Preserve explicit issuer/subject matching and assurance requirements. Include a real browser exercise through the deployed HTTPS proxy with at least one supported IdP.

**Acceptance.** Parallel exchange attempts yield at most one token pair. Reuse after a successful exchange fails. Invalid state, wrong issuer/audience, missing assurance, unlinked or disabled accounts, and unlinking are covered. The operator can revoke access within the documented window when an IdP account is disabled.

### R15 · P1 · Close tenant lifecycle and access boundaries

**Evidence.** The controllers check membership and PostgreSQL RLS enforces the document tenant on raw queries. Membership changes and tenant selection are separate from the document transaction. The public service contracts accept a tenant ID, and the directory currently has create/list/member set/remove without tenant offboarding or a tenant-scoped admin model. The generic session checks RLS for one declared relation. [TenantDocumentsController.cs](../src/Modules/SuperBackend.Tenancy/Controllers/TenantDocumentsController.cs#L25) · [TenantDataSession.cs](../src/Modules/SuperBackend.Tenancy/Persistence/TenantDataSession.cs#L17) · [TenantDirectoryController.cs](../src/Modules/SuperBackend.Tenancy/Controllers/TenantDirectoryController.cs#L16) · [PostgreSQL RLS behavior](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)

**Task.** Define who owns tenant provisioning, valid member identities, removal, suspension, and export/deletion. Specify whether a concurrent in-flight operation may complete after revocation. Use an authorized tenant scope or a module entry-point pattern that keeps membership checks explicit for future tenant-backed features. Verify least-privilege grants and policies for all tenant relations.

**Acceptance.** Tenant A cannot access Tenant B through HTTP, raw data access with runtime credentials, or a background path. A pooled connection loses tenant scope after commit/rollback. Invalid roles and unknown users are rejected according to policy. Tenant offboarding is reproducible. Notes remains documented as owner-scoped unless a product intentionally implements tenant Notes.

### R16 · P1 · Prevent incompatible workers from consuming work

**Evidence.** The claim SQL selects any available job without a handler-type filter. The worker discovers handlers after claiming the row, and missing handlers spend attempts. Notes documentation currently relies on all active worker replicas registering the handler. This is fragile during rolling deployment or split worker topologies. [PostgresJobQueue.cs](../src/Modules/SuperBackend.Jobs/Persistence/PostgresJobQueue.cs#L20) · [JobWorker.cs](../src/Modules/SuperBackend.Jobs/Services/JobWorker.cs#L42) · [TRANSACTIONAL-OUTBOX.md](../docs/TRANSACTIONAL-OUTBOX.md#L93)

**Task.** Explicitly define a homogeneous worker rollout contract or introduce type-aware claiming. Validate handler registration and job payload compatibility at startup or deployment. Define how handlers and their payload schemas evolve while pending work exists. Reuse lease fencing and inbox semantics.

**Acceptance.** A mixed-version rollout with pending jobs either fails deployment validation safely or routes jobs only to capable workers. Existing events retain compatible handlers until drained. Tests cover unsupported types, lease loss followed by reclaim, and duplicate delivery without double effects.

### R17 · P1 · Give queue and outbox failures an operator workflow

**Evidence.** Jobs exposes a list and dead-job retry. The list is offset-only, redrive resets attempts, and outbox conflicts are terminal rows inspected using SQL. There is no supported filter/detail flow, replay reason, retention tool, or delivery trail across attempts. [JobsController.cs](../src/Modules/SuperBackend.Jobs/Controllers/JobsController.cs#L11) · [PostgresJobQueue.cs](../src/Modules/SuperBackend.Jobs/Persistence/PostgresJobQueue.cs#L72) · [PostgresOutboxRelay.cs](../src/Modules/SuperBackend.Outbox/Persistence/PostgresOutboxRelay.cs#L9)

**Task.** Implement the smallest supportable operator surface for inspection, stalled backlog, dead-letter redrive, conflicts, and retention. That can be an authenticated API with a runbook. Preserve payload privacy and distinguish re-running an existing ID from submitting a new logical job. Record actor and reason, and audit all redrive actions.

**Acceptance.** Operators can locate a job/message by ID, status or age and recover from a documented failure without ad hoc writes. A redrive cannot overwrite a successful event or defeat an inbox receipt. An interrupted cleanup or retry can be resumed safely. Backlog, conflict, and old-job cases are exercised.

### R18 · P1 · Verify the enabled admin UI as a first-party client

**Evidence.** The browser panel handles identities, roles, and audit. API integration tests cover its shell, while the committed CI workflow has no browser journey. Optional module administration is mainly API-based. [panel.js](../src/SuperBackend.Api/wwwroot/admin/panel.js#L72) · [BackendTests.cs](../tests/SuperBackend.IntegrationTests/BackendTests.cs#L212) · [ci.yml](../.github/workflows/ci.yml#L14)

**Task.** State which operator operations are browser-supported and which have a supported API/runbook. Add a narrow browser regression test for login/MFA, privilege changes, stale or revoked sessions, and EN/FA navigation. Make keyboard and real screen-reader checks part of the release profile if the UI is advertised.

**Acceptance.** A real browser journey passes behind the supported HTTPS setup. An ordinary account has no operator access. Session expiry and failure paths have clear UI feedback. The localized UI is navigable with a keyboard. Product-only dashboards remain outside the starter scope.

## Milestone 4 — Simplify and prevent scope growth

These are consolidation tasks. They should preserve verified behavior. Each change needs a reason tied to one of the preceding gates or to measured time/infrastructure cost.

### R19 · P2 · Keep a thin host and a single configuration reference

**Evidence.** `Program.cs` maintains feature flags, service dependencies, migration ordering, and routing. The database script repeats one feature dependency; settings are also repeated across README, module documentation, Compose, and CI. Explicit registration remains readable at the present size. [Program.cs](../src/SuperBackend.Api/Program.cs#L23) · [db.sh](../scripts/db.sh#L26)

**Task.** Keep explicit registration. Extract a small shared configuration/validation definition only for settings now repeated across startup and migration. Use one canonical settings table. Keep README as an entry point.

**Acceptance.** Invalid flag combinations fail consistently in run/migrate/profile checks. Default and all-feature behavior is unchanged by the refactor. No plugin discovery, service locator framework, or generic migration DSL is introduced.

### R20 · P2 · Remove generated ceremony that does not protect a boundary

**Evidence.** The service generator produces a controller, contract, model, implementation, module wrapper, and one status route by construction. The shape demonstrates the requested directory convention but can be cargo-culted into interfaces and implementations with no separate role. Existing modules use a mixture of MVC controllers and Minimal APIs. [new-service.sh](../scripts/new-service.sh#L60) · [ARCHITECTURE.md](../docs/ARCHITECTURE.md#L61)

**Task.** Keep the documented folders available and keep the generator. Require contracts for cross-module, infrastructure, or substitution boundaries. Generate a small protected route as the exemplar and document when a module can leave a folder unused. Retain existing endpoint styles until there is a concrete contract or maintenance reason to converge.

**Acceptance.** The starter still generates a useful, authorized module in one command. The scaffold tracks only files that demonstrate a real boundary. New modules can follow one documented HTTP contract and testing convention without a broad rewrite.

### R21 · P2 · Keep examples optional and trim duplicate reference material

**Evidence.** Notes demonstrates private ownership, optimistic concurrency, and Outbox/Inbox. Tenant Documents demonstrates the different RLS boundary. Both are shipped as optional sample behavior with tests. Notes adds a historical counter used to demonstrate idempotent effects, outside core identity or platform services. [ARCHITECTURE.md](../docs/ARCHITECTURE.md#L23) · [TRANSACTIONAL-OUTBOX.md](../docs/TRANSACTIONAL-OUTBOX.md#L81)

**Task.** Retain each example's distinct lesson and offer a documented minimal generated profile with the examples disabled. Remove duplicated prose and ownership CRUD examples only when the corresponding security tests stay in the reference suite. Clarify that the Notes counter illustrates processing behavior and historical events.

**Acceptance.** A minimal profile can run and migrate without installing optional routes or workers. The full reference still proves ownership, RLS, and durable at-least-once processing. Named generation produces only the promised files and capabilities for the selected profile.

### R22 · P2 · Remove accidental operational duplication and growth paths

**Evidence.** CI separately invokes several migration checks that each build startup projects and restore tools. Job workers poll and run a bulk expired-lease sweep on each claim cycle. Offset listing and some browser role lookups scale with stored history. [ci.yml](../.github/workflows/ci.yml#L14) · [PostgresJobQueue.cs](../src/Modules/SuperBackend.Jobs/Persistence/PostgresJobQueue.cs#L20) · [panel.js](../src/SuperBackend.Api/wwwroot/admin/panel.js#L304)

**Task.** Measure the baseline first. Reuse the CI build for drift checks where the EF tool permits it. Limit or schedule maintenance queries when measurements show meaningful pressure. Prefer indexed, keyset-style operator pagination for growing history. Avoid implementing bespoke caches and schedulers to address an unmeasured problem.

**Acceptance.** Pre/post measurements show the removed cost. CI still checks every schema. Growing history stays within the chosen latency and query budgets. Worker recovery and replay tests continue to pass.
