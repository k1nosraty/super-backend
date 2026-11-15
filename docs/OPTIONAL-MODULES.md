# Optional modules

The host registers a module's controllers, services, and permissions only when its flag is enabled. The three flags default to false in every environment.

| Flag | Capability | Schema |
| --- | --- | --- |
| `Modules__Federation=true` | Organization sign-in with OpenID Connect | Existing `identity` external login table |
| `Modules__Tenancy=true` | Tenant directory, tenant membership, and PostgreSQL-isolated documents | `tenancy` |
| `Modules__Jobs=true` | PostgreSQL job queue, durable worker claims, and operations API | `jobs` |

Run the migration command with the same module flags used by the service. With the SDK and the local PostgreSQL container:

```bash
Modules__Tenancy=true Modules__Jobs=true ./scripts/db.sh apply
```

For deployed images, pass the configuration to the one-shot `--migrate` process. In Docker Compose development, use an override file to pass the settings to both `migrate` and `api`. Switching off a module leaves its data and migration history available for future reactivation.

## Organization sign-in

Register this application as a **confidential OpenID Connect web client** with the identity provider. Configure authorization code flow, PKCE with S256, and the callback `{Identity__PublicOrigin}/api/v1/oidc/callback`. Use HTTPS in deployed environments. Development supports HTTP discovery only on loopback.

| Setting | Purpose |
| --- | --- |
| `Oidc__Authority` | OIDC discovery authority |
| `Oidc__Issuer` | Exact issuer from discovery or the ID token, including any trailing slash. Defaults to the authority without a trailing slash. |
| `Oidc__ClientId` | Client ID registered at the provider |
| `Oidc__ClientSecret` | Client secret from the deployment secret store |
| `Oidc__RequiredAcr` | Optional exact `acr` claim value required for sign-in |
| `Identity__PublicOrigin` | Canonical first-party origin used for the callback URI and exchange origin check |

Configure provider MFA or equivalent organizational sign-in policy before linking accounts. Set `Oidc__RequiredAcr` when your provider publishes the required assurance class. The provider handles the organizational authentication factors. The application still evaluates its own memberships and permissions.

1. Create and confirm a regular local account. This can use the existing invitation flow.
2. As a local administrator, send `PUT /api/v1/admin/oidc-links` with `{"userId":"<local-id>","subject":"<provider-sub>" }`. Obtain the subject from a trusted provider directory or verified ID token. Linking is based on the configured issuer and the exact subject. The external email, roles, and groups confer no local access. Operator accounts with the built-in Administrator role use local sign-in.
3. Navigate to `GET /api/v1/oidc/login` from the browser. The provider returns to the callback, which validates protocol state and nonce, signed ID token, audience, and issuer. The browser then reaches `GET /api/v1/oidc/session` on the same origin.
4. A first-party browser client can call `POST /api/v1/oidc/exchange` with credentials enabled. The origin must match `Identity__PublicOrigin`. The response is the normal local access/refresh token pair, with local API authorization semantics. The HTTP-only exchange cookie expires after two minutes and is cleared after exchange. Provider tokens are not persisted or returned to the client.
5. Remove a link with `POST /api/v1/admin/oidc-links/remove` and the same body. This invalidates previously issued local sessions. Suspend a local user or revoke their sessions through the existing administration API.

The starter includes the challenge, callback, and session exchange endpoints. A product's first-party UI owns post-login navigation and can use a popup or a first-party continuation that calls the exchange endpoint. Local token revocation does not end the provider's browser session. This module uses one explicitly configured issuer per deployment. [ASP.NET Core OIDC configuration](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)

## Multitenancy and row security

The tenancy module uses a shared PostgreSQL database with explicit tenant IDs, a membership table, composite document keys, and forced PostgreSQL row-level security (RLS). Global accounts, operator data, and the original owner-private Notes demonstration continue to use their own existing boundaries. Put tenant business data in tenant-scoped tables that enforce this pattern.

Apply the Tenancy migrations before provisioning the runtime data connection. Use a dedicated database login that cannot bypass row security. For example, run the following in an administrative `psql` session on the application database, with a role name that fits your environment:

```sql
CREATE ROLE app_tenant_runtime LOGIN NOSUPERUSER NOBYPASSRLS;
GRANT USAGE ON SCHEMA tenancy TO app_tenant_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy."Documents" TO app_tenant_runtime;
\password app_tenant_runtime
```

Set `ConnectionStrings__TenantData` to that login's connection string. Keep `ConnectionStrings__Main` for the core application and tenant directory. A least-privilege runtime credential can use `Main` for both connections when PostgreSQL reports RLS active; the default development database owner is a superuser and cannot be used for tenant data. A missing policy or bypassed RLS causes the readiness check to fail and tenant document operations to reject the unsafe connection.

| Operation | Route and requirements |
| --- | --- |
| Create a tenant | `POST /api/v1/tenants` with `{"name":"Example"}`, requires `tenancy.manage` |
| List caller memberships | `GET /api/v1/tenants?offset=0` |
| Add or update membership | `PUT /api/v1/tenants/{tenantId}/members/{userId}` with `{"access":"Reader"}` or `{"access":"Editor"}`, requires `tenancy.manage` |
| Remove a membership | `DELETE /api/v1/tenants/{tenantId}/members/{userId}`, requires `tenancy.manage` |
| List or create documents | `GET` or `POST /api/v1/tenants/{tenantId}/documents`; creation requires Editor |
| Read, update, or delete a document | `GET`, `PUT`, or `DELETE /api/v1/tenants/{tenantId}/documents/{id}`; writes require Editor |

The tenant route selects a candidate tenant. Authorization independently checks active membership. Platform administrators need an explicit membership to access document data. Both missing rows and inaccessible tenant data return `404`. Each request sets its tenant in a transaction-local PostgreSQL setting, so pooled connections cannot retain another request's scope.

For additional features, inject `ITenantAccess` and `ITenantDatabaseSession` from the Tenancy module. After checking membership, pass the server-selected tenant ID and a trusted schema-qualified relation to `ExecuteAsync`. The callback receives the runtime connection and transaction. Enforce a `TenantId` column, tenant-aware unique keys and foreign keys, and an `ENABLE` / `FORCE ROW LEVEL SECURITY` policy with both `USING` and `WITH CHECK`. Grant the runtime role access only to appropriately protected tables. The session checks the declared protected relation, so apply and verify policies on every relation reached by the callback. [PostgreSQL row-security policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)

## Durable jobs

Enable the module and apply its migrations. Inject `IDurableJobQueue` into a product service to enqueue work. Register a scoped `IDurableJobHandler` for each job type on the worker host. Type names must be unique in the handler registry.

```csharp
builder.Services.AddScoped<IDurableJobHandler, RebuildReport>();
```

```csharp
await queue.EnqueueAsync(operationId, "reports.rebuild",
    JsonSerializer.SerializeToElement(new { reportId }), maxAttempts: 5, ct);
```

Use a stable, nonempty job ID as the idempotency key. The first insert is retained when the same ID is submitted again. An enqueue call commits its queue record independently from other modules' writes. When both writes must be atomic, build a transactional outbox against the originating data store.

Workers use PostgreSQL `FOR UPDATE SKIP LOCKED` and bounded leases. A worker claims a job with a unique lease token, and only that unexpired lease can mark its result. Unfinished leases become eligible for recovery. Failed jobs retry with bounded exponential delay and end in `dead` after their configured attempt budget. Delivery is **at least once**. Make handler side effects idempotent using `JobExecution.Id`. Propagate cancellation to downstream calls and keep attempts within the configured deadline.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Jobs__RunWorker` | `true` | Set false for an enqueue-only instance |
| `Jobs__PollMilliseconds` | `500` | Poll delay when there is no ready job |
| `Jobs__LeaseSeconds` | `60` | Lease before another worker can reclaim the job |
| `Jobs__AttemptTimeoutSeconds` | `45` | Cooperative execution deadline, strictly shorter than the lease |
| `Jobs__RetrySeconds` | `5` | Initial backoff, doubled to a maximum of five minutes |

Each host runs one consumer loop, and multiple instances can compete for work. `GET /api/v1/admin/jobs?offset=0` requires `jobs.inspect`; `POST /api/v1/admin/jobs/{id}/retry` requires `jobs.retry` and requeues a dead job with a fresh attempt budget. The operations API omits payloads. Payloads remain in the database subject to the deployment's retention and redaction practices. Retain job IDs for the full deduplication window.
