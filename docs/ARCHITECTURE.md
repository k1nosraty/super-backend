# Architecture

```text
src/
  SuperBackend.Api/           Composition root, HTTP pipeline, administration assets
    Infrastructure/          Security headers, localization, OpenAPI, telemetry
    wwwroot/admin/           First-party browser client for the administration API
  SuperBackend.Platform/     Permission contract, validation filter, shared resources
  Modules/
    SuperBackend.Federation/ Optional OIDC protocol, local account linking
    SuperBackend.Identity/   Accounts, administration, SMTP, PostgreSQL persistence
    SuperBackend.Jobs/       Optional PostgreSQL queue and worker
    SuperBackend.Notes/      Optional feature with ownership and concurrency checks
    SuperBackend.Outbox/     Optional transactional writer, relay, and inbox
    SuperBackend.Tenancy/    Optional tenant directory, membership, and RLS
tests/
  SuperBackend.IntegrationTests/
scripts/                     Template generation and database operations
docs/                        Architecture and operations
```

The host references the modules and registers the enabled features. Modules do not reference one another's EF models. Federation uses the explicit account-linking contract in Identity. The Identity module owns the permission handler and queries current grants for every protected operation. The Notes module declares permissions without a dependency on Identity. Browser operations call the same authorization and auditing code as other clients.

## Add a feature module

1. Create a project at `src/Modules/SuperBackend.Orders`. Add it to the solution and reference it from the API host. Use separate endpoint and persistence files as the module grows.
2. Register its services and data context at startup, then map its endpoints. For optional modules, use the same flag for registration, routing, and migrations.
3. Register each permission in the host's authorization builder. Check ownership or other resource-level scope when querying individual records.
4. Apply Data Annotations to request DTOs and attach `ValidateBody<T>()` to the endpoint. Keep response contracts separate from EF entities.
5. Give the new context its own PostgreSQL schema and EF migration history table. Add it to the migration command and its health check to the readiness probe. Run `dotnet restore` and commit the updated package locks.

```csharp
services.AddAuthorizationBuilder()
    .AddPermission("orders.read", "Read orders");

app.MapGet("/api/v1/orders", (ClaimsPrincipal user, OrderQueries orders, CancellationToken ct) =>
    orders.ListForUserAsync(user.UserId(), ct))
    .RequireAuthorization("orders.read");
```

The Notes module shows CRUD, offset pagination, identity ownership, and an optimistic version token. Set `Modules__Notes=false` to disable it. The administrative permission catalog follows the active modules. A switched-off module's data and previously configured grants remain in PostgreSQL.

## Data ownership

| Schema | Tables and responsibilities |
| --- | --- |
| `identity` | ASP.NET Core Identity users, roles, claims, login, role membership, and user token tables. User rows also hold display name and suspension state. |
| `identity` | `RolePermissions` maps module permission identifiers to roles. Its composite key prevents duplicate grants. `AccessAudits` stores operator actions without foreign keys to records that can later be deleted. |
| `notes` | `Notes` holds owner ID, title, body, creation time, and version. The module queries within the caller's owner ID before returning or updating records. |
| `tenancy` | Tenant directory, memberships, and documents protected by PostgreSQL RLS. |
| `jobs` | Durable job payloads, retry state, and lease metadata. |
| `outbox` | Job submissions, dispatch status, and per-consumer deduplication receipts. |
| Each schema | Independent `__EFMigrationsHistory`. |

## Add a language

Register the culture in `src/SuperBackend.Api/Infrastructure/ApiLocalization.cs`. Add a sibling `BackendText.<culture>.resx` under `src/SuperBackend.Platform/Resources`. The default resource is English. Feature code reads these resources with `IStringLocalizer<BackendText>`.

For the administration client, add a dictionary in `wwwroot/admin/locales.js`, an option in the language selector, and update direction handling for any additional RTL culture. System-generated Identity error details and third-party package messages retain their framework behavior.

The bundled Vazirmatn font is distributed under SIL Open Font License 1.1. Its [license](../src/SuperBackend.Api/wwwroot/admin/fonts/OFL.txt) accompanies the asset.

## Scaffold a service

```bash
./scripts/new-service.sh InventoryReports
```

The scaffolder copies the [service template](../templates/service/) into `src/Modules/SuperBackend.InventoryReports`, adds the project to the solution, adds the API reference, and wires the module into `ProductModules.cs`. It then restores dependency locks. Use Bash, Python 3, and the .NET SDK. It rejects an existing module without overwriting it.

| Folder | Responsibility |
| --- | --- |
| `Controllers/` | HTTP routing, authorization and response status mapping |
| `Contracts/` | Service interfaces consumed by controllers and other modules |
| `Models/` | Request, response, and domain records |
| `Services/` | Feature and application behavior |
| `Persistence/`, `Migrations/` | Add these when the module owns persistent data. |

Set `Modules__InventoryReports=true` to register the sample route `GET /api/v1/features/inventory-reports` and the permission `inventory-reports.read`. Grant that permission using the administration API or panel. The scaffold's small status handler is the part to replace with product behavior. For an EF-backed service, follow the existing Jobs or Tenancy migration setup and register your migration in the host's `--migrate` branch. The database script also accepts the new module name after its context has been wired.
