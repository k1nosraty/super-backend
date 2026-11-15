# Release acceptance execution record

**Date:** 2026-11-15 (UTC). **Scope:** GitHub-hosted release verification and an isolated, loopback-only lab using synthetic accounts and records. Production endpoints, operational notification destinations, and customer records were outside this execution. The environment choice applies to the backup, fault injection, and erasure drills below.

## GitHub Actions

| Check | Result | Evidence |
| --- | --- | --- |
| Build and integration | Pass | [Run 43720347129](https://github.com/k1nosraty/super-backend/actions/runs/43720347129) |
| Release evidence | Pass | [Run 43720347126 and release-evidence artifact](https://github.com/k1nosraty/super-backend/actions/runs/43720347126) |
| Tested source | `2e19ed1fa5d2da4d6953276f0edd821e93174216` | [Acceptance branch](https://github.com/k1nosraty/super-backend/tree/release-acceptance/v1) |
| Previous schema | `9c587e2d820635b2ecd98994bf6667a2f48f5da4` | Runner artifact `source.txt` and `upgrade.json` |
| Candidate image ID on runner | `sha256:5b05156d79b21da9dcc90e4e396c0fcb064f45321520a3401a1b4b59fdbbaa41` | Runner artifact `fresh.json` |
| Tests and advisory scans | 23 passed, zero failed. Zero HIGH/CRITICAL findings reported by Trivy and zero NuGet advisories returned. | Runner artifact `release.trx`, `image-vulnerabilities.json` and `nuget-advisories.json` |
| Hosted deployment fixtures | Fresh and upgrade rehearsals passed. The upgrade preserved the previous-schema record. Both replicas served traffic and the pool drill reached 10 blocked connections on a replica. | Runner artifact `fresh.json`, `upgrade.json`, `rehearsals.log` and the history execution plans |

The [first release run](https://github.com/k1nosraty/super-backend/actions/runs/43719938167) failed in the combined browser and deployment rehearsal step after the tests, builds, and scan passed. The successful candidate uses Chrome already installed on the Ubuntu 24.04 runner and uploads rehearsal output for inspection. The repository default branch was left at its existing revision.

## Organization sign-in and deprovisioning

A separate Keycloak 26.8.2 instance served HTTPS discovery to the application and authenticated a synthetic subject in Chromium. The client required authorization code flow with PKCE S256. The browser used Keycloak's Pushed Authorization Request URL. An unlinked subject received 401. After local linking, two parallel handoff exchanges returned 200 and 401, and replaying the original handoff cookie returned 401. The resulting bearer resolved to the expected local user.

Disabling the subject in Keycloak blocked new provider login. The previously issued local bearer continued to return 200 until the operator removed the local link. The immediate follow-up request returned 401, observed 0.019 s after starting the unlink operation. This establishes the manual revocation path. A target provider disable feed, its service account and timing, and the target assurance policy remain deployment inputs. [Keycloak container and realm configuration](https://www.keycloak.org/server/containers)

## Authenticated SMTP and monitoring

Mailpit 1.33.1 listened on an isolated SMTP endpoint and required STARTTLS plus credentials. The SMTP client rejected a pre-TLS mail attempt with 530 and an incorrect password with 535. The application's queued confirmation reached the test mailbox over the configured authenticated SMTP connection and confirmed the synthetic account. During a stopped SMTP container, the application accepted registration, reported a delivery failure, and sent the confirmation after SMTP restarted. The recovered account then signed in. [Mailpit STARTTLS and authentication](https://mailpit.axllent.org/docs/configuration/smtp/)

Prometheus 3.10.0 evaluated the readiness, outbox age, and dead-job probes and the application's exported SMTP failure metric. Alertmanager 0.32.1 delivered eight events to the lab webhook. The live endpoint continued returning 200 during the database outage. The paused relay left the message visible in the operator API until restart, after which its job settled. The deliberately failed job reached its terminal dead state and was visible in the operator API. [Prometheus alert rules](https://prometheus.io/docs/prometheus/latest/configuration/alerting_rules/) · [Alertmanager configuration](https://prometheus.io/docs/alerting/latest/configuration/)

| Alert | Firing received (UTC) | Resolved received (UTC) |
| --- | --- | --- |
| SMTP delivery failure | 13:45:07.599 | 13:45:18.606 |
| Database readiness failure | 13:45:48.595 | 13:45:49.595 |
| Outbox relay paused | 13:45:55.595 | 13:45:57.595 |
| Dead durable job | 13:46:00.595 | 13:46:01.595 |

## Backup and restore at lab scale

PostgreSQL 18.6 held **1,241,970,367 bytes** (1.16 GiB), including **240,000 Notes rows** with 3,600-character high-entropy bodies and **100,000 settled Jobs rows**. A custom-format archive of **601,909,993 bytes** took **7.666 s** to create. Restoring into a separate PostgreSQL container and checking the recovered application took **23.302 s**.

The two table counts and sampled note SHA-256 hashes matched. The transaction committed before backup was recovered and the post-backup marker was absent. At the simulated cutoff, the recovered marker was **53.938 s** old. The recovered API became ready and accepted a pre-backup bearer against the separately restored Data Protection key ring. These measurements describe the synthetic logical-backup drill. The deployment owner must supply the approved RPO/RTO and backup or PITR mechanism for deployment sign-off.

## Scoped data erasure

An operator disabled a synthetic account and applied a single guarded transaction scoped to the account, its tracked queued mail, owned note, and one designated synthetic tenant. Target counts reached zero for identity, sessions, user-role mappings, owned notes, Notes idempotency receipt and aggregate, the linked outbox message and inbox receipt, fixture jobs, tenant documents, and memberships. The disabled-account audit event remained. The old access and refresh credentials returned 401, the control account's note stayed accessible, and a control tenant's document remained. The isolated SMTP inbox no longer contained the target confirmation after deletion by message ID.

This was an operator exercise on disposable data. A deployment's erasure runbook must name retained Audit/backup records, each product-owned data store, applicable retention, and the allowed target. Encrypted queued mail was deleted using a job ID captured during fixture creation. Product erasure needs a way to locate every in-scope operation for the subject.

## Inputs for a future target-environment run

| Area | Required to execute |
| --- | --- |
| Deployment | Staging URL and expected API image digest, runner with network reachability, trusted CA, and a least-privilege staging operator account. |
| Identity provider | Authority and expected issuer, client ID and secret-store reference, required ACR, registered callback URI `/api/v1/oidc/callback`, two staging subjects, authority to disable a test subject, and the expected delay for provider disable to reach the application. |
| SMTP | SMTP hostname, port, TLS mode and CA, verified sender, secret-store references for credentials, one controlled inbox in which delivery and provider events can be checked. |
| Monitoring | Collector/read access to metrics, the staging notification route, and permission to inject database, SMTP, relay and job failures within an agreed exercise window. |
| Recovery | Source snapshot or archive reference and timestamp, target size and composition, private restore destination, read and restore permissions, Data Protection key-ring backup and access, and approved RPO/RTO. |
| Erasure | A synthetic account and tenant with their exact IDs, inventory of product-owned stores and mail references, permitted deletion scope and execution role, and approved retention exceptions for Audit and backups. |

Provide names and paths to managed credentials. Do not add credential values to this public repository or to Actions artifacts.
