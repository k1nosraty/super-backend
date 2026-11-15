# Release evidence

The [release tasks](RELEASE-TASKS.md) preserve the 22 original findings and their acceptance criteria. The checks below were run against local PostgreSQL, Mailpit, and the two-replica reference deployment on November 15, 2026. The workflow produces its own clean-runner results for the commit being released.

| Task | Local implementation and verification | Further release evidence |
| --- | --- | --- |
| R01 | Expected migration IDs across enabled contexts. PostgreSQL tests cover empty, partial, current, compatible newer, and unavailable databases. The deployment drill confirms readiness fails and liveness survives an outage. | Run the profile against each environment's database before routing traffic. |
| R02 | Production permissions require MFA. Integration tests cover enrollment, preserved assurance on refresh, audited MFA reset, revocation and last-operator suspension protection. The HTTPS and browser fixtures enroll and use TOTP. | Provision and verify two named operators in the deployment. |
| R03 | Revocable database-backed sessions, single-session logout, and atomic one-time refresh. Parallel replay and single-session isolation are tested. The same session survives two API replacements in the reference deployment. | Plan the first sign-in after the bearer-format upgrade. |
| R04 | Reproducible HTTPS profile, separate DDL and runtime roles, baseline upgrade with a preserved row, fresh install, replica replacement, and isolated restore with a preserved indicator. | Approved RTO and RPO, realistic-volume backup/PITR and shared key-ring restore in the target environment. |
| R05 | Integration tests compare generated operation metadata and runtime status/body behavior for core paths. Localized errors include stable `code` and `traceId`. | Review versioning before incompatible contracts are introduced. |
| R06 | Framework Identity route inventory is explicit. The test suite covers malformed bodies, media type handling, input limits, password step-up, MFA and reset behavior. | Review the endpoint inventory on .NET upgrades. |
| R07 | Optional encrypted mail queue and atomic invitation creation. PostgreSQL and Mailpit verify SMTP failure, retry after replacing the worker, expired envelopes skipped during SMTP failure, no plaintext in queue payloads, and acceptance using the delivered code. | Validate the target SMTP provider and alert routing. |
| R08 | Owner-scoped Notes idempotency commits its receipt and optional outbox with the note. Concurrent/replayed requests return one ID and conflicting reuse returns 409. Tenant ETag writes reject stale versions. | Choose production retry and retention windows. |
| R09 | Audited lifecycle, MFA recovery, queue retry and outbox redrive. The reference database grants append/read access for audit. The staging fixture runs maintenance twice, removes the settled job, outbox message and inbox receipt together, preserves pending work, and rejects audit deletion with the runtime credential. | Exercise product export/erasure and audit archive retention on representative data. |
| R10 | Bounded outcome and duration metrics for jobs, outbox, and SMTP. Local database outage and failed SMTP drills, protected metadata, and Notes-to-job correlation. | Verify monitor rules, notifications, and stopped-relay/dead-job alert drills in the collector environment. |
| R11 | The two-replica TLS fixture records latency, production and drain interval, sampled active PostgreSQL connections, and aggregate 429s for clients sharing one source IP with different forwarded headers. | Approve latency/throughput budgets and measure sustained performance on target hardware, proxy topology, and traffic mix. |
| R12 | Pinned CI actions, scheduled dependency updates, freshly applied and previous-schema deployment rehearsals. Workflow gathers source refs, local image IDs, TRX, rehearsal outcomes and scan report. | The repository maintainer reviews the clean-runner result, registry digest, and weekly SDK/runtime/base-image updates. |
| R13 | Reject reserved keywords, incompatible segments, lengthy names and existing paths. `Example.Inventory` and `Northwind.Dispatch` pass locked restore, Release build, fresh migration and HTTP/OpenAPI checks. | Generated applications should assign their own release history before adopting the release workflow. |
| R14 | One-time database-backed OIDC exchange. Signed provider fixture verifies concurrent exchange, replay, invalid state, wrong issuer/audience, missing assurance, disabled accounts, local linking/unlinking, and origin policy. | Real tenant-specific provider and disable-feed validation against the agreed deprovisioning window. |
| R15 | Membership checks, audited suspension/reactivation, unknown user and role rejection, owner-scoped Notes, and direct and background PostgreSQL RLS coverage, and tenant scope reset after commit and rollback. | Exercise any product-specific tenant background worker and offboarding export/erasure. |
| R16 | Workers claim supported types only. Tests cover unknown types retaining attempts, lease expiration and reclaim, retry and inbox deduplication. | Keep compatible handlers available until the previous release's queue drains. |
| R17 | Filterable metadata and audited redrive. Tests cover one replacement per outbox conflict, existing inbox receipt protection, and queue retry after failure. | Review long-running backlogs and disposition rules with operators. |
| R18 | Chromium against the reference HTTPS profile verifies MFA sign-in, ordinary-account denial, revoked-session feedback, keyboard-driven language selection, RTL, and server-backed logout. | Confirm the required browser and assistive-technology matrix for the deployment. |
| R19 | One feature profile validates module dependencies for app startup, migration, and profile checking. | Configure environment-specific features and run `--check-profile` with release settings. |
| R20 | Optional lean authorized controller scaffold and full scaffold. Both are built in named generated solutions. | Apply the documented endpoint and permission tests to generated features. |
| R21 | Minimal and reference generated profiles migrate, start and expose the expected OpenAPI surface. Full integration cases retain ownership, RLS and outbox delivery semantics. | Select the profile before supplying target resources and optional integrations. |
| R22 | CI reuses a Release build for five EF drift checks. Expired leases are swept on an interval in bounded batches. Existing replay/recovery tests pass and the rehearsal captures an execution plan with a configurable synthetic queue history. | Evaluate plans and retention against production-sized history and the accepted query budget. |

## Measured local runs

| Check | Observed |
| --- | --- |
| .NET Release integration suite | 23 passed |
| Generated projects | Reference `Example.Inventory` and minimal `Northwind.Dispatch` verified through HTTP |
| Clean and upgrade deployment | Two API replicas, HTTPS and MFA, schema migration, previous Notes row preserved on upgrade, runtime DDL rejected, preserved session across replacement |
| Database outage and recovery | Readiness returned 503 during outage, liveness returned 200, readiness returned to 200 after restart |
| Isolated restore | Approximately 0.50 s (fresh) and 0.33 s (paced upgrade fixture). Both recovered the committed indicator. |
| Note create p95 | Approximately 123 ms for 12 unpaced requests and 22 ms for 120 requests paced at 10 requests/s |
| Shared-IP ingress test | Example runs throttled 66/100 on the fresh fixture and 57/100 on the paced upgrade fixture |
| Durable processing and pool | The upgrade fixture issued 120 note writes in 12 seconds and settled all jobs within another two seconds. A separate table-lock drill reached the limit of 10 connections on a replica and all 30 writes succeeded after release. |
| Queue history fixture | Claim-candidate query executed in 0.052 ms with 25,000 synthetic settled jobs and 25 pending candidates. The release workflow uploads the measured execution plan. |
| Advisory checks | Trivy reported zero HIGH/CRITICAL findings for the locally scanned candidate image `sha256:4f61c473c161aa8febd28b3df3ea7938421c79fcfdc334f94ef67fe833d4c9fc`. The direct/transitive NuGet advisory query returned no entries. |
| EF drift checks | 12.79 s rebuilding by default; 7.07 s using the existing Release build in the same workspace. The latter timing excludes the already-required Release build. No pending model changes in the five schemas. |

These are measurements for the small local fixtures. The recovered indicator establishes that one committed point survived that backup. The deployment owners set and validate their own recovery, latency and sustained-load objectives.

## Reproduce

```bash
docker compose up -d --wait database mail
dotnet restore --locked-mode
dotnet test -c Release --no-restore
dotnet format whitespace --no-restore --verify-no-changes
for module in Identity Notes Tenancy Jobs Outbox; do
    DB_CHECK_CONFIGURATION=Release ./scripts/db.sh check "$module"
done
./scripts/check-generated.sh

API_IMAGE=superbackend-rehearsal:candidate \
REHEARSAL_EVIDENCE_FILE=/tmp/fresh.json ./scripts/rehearse-release.sh

# Build the previous image from a checked-out released ref before running the upgrade.
API_IMAGE=superbackend-rehearsal:candidate PREVIOUS_IMAGE=superbackend-rehearsal:previous \
REHEARSAL_EVIDENCE_FILE=/tmp/upgrade.json ./scripts/rehearse-release.sh
```

See [the deployment runbook](OPERATIONS.md) for secret injection, browser setup, production criteria, recovery, and alert drills. [Trivy database sources](https://trivy.dev/docs/latest/configuration/db/) · [Release evidence workflow](../.github/workflows/release-evidence.yml)
