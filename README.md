# SuperBackend

SuperBackend is a reusable C# and .NET 10 backend starter. It runs as a modular monolith with PostgreSQL. Each feature module owns its endpoints, persistence model, and migrations. A new product can start with account management, access control, and operations tooling, then add its own feature modules under `src/Modules`.

## Run locally

```bash
docker compose up --build -d
curl --fail --retry 8 --retry-delay 1 --retry-all-errors http://localhost:5080/health/ready
```

Open the [administration panel](http://localhost:5080/admin), [OpenAPI document](http://localhost:5080/openapi/v1.json), or [local mail inbox](http://localhost:8025). Compose applies the enabled modules' migrations before it starts the API. The ports bind to loopback. Copy `.env.example` to `.env` to customize the local ports.

For SDK development, use Docker only for PostgreSQL and Mailpit. Stop the Compose API first if it already occupies port 5080.

```bash
docker compose up -d --wait database mail
dotnet restore --locked-mode
./scripts/db.sh apply
dotnet run --project src/SuperBackend.Api
```

The local scripts use Bash. On Windows, run them from WSL.

### Create the first administrator

Run this after migrations have completed. The administrator's email is marked confirmed by this trusted operator command. Choose a password with at least 12 characters.

```bash
read -rsp "Administrator password: " Bootstrap__Password
printf '\n'
export Bootstrap__Password
docker compose run --rm --no-deps -e Bootstrap__Password migrate --bootstrap-admin admin@example.test
unset Bootstrap__Password
```

For SDK development, replace the Docker command with:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SuperBackend.Api --no-launch-profile -- --bootstrap-admin admin@example.test
```

Sign in at `/admin`. The Users view supports prefix search, email invitations, role membership, suspension, and session revocation. The Roles view manages roles and permissions. The Audit log records access and account administration changes. The language selector switches between English and right-to-left Persian. The panel holds its tokens in memory for the current tab. Reloading the page ends the browser session.

## Generate a named solution

Run this from a checkout of the starter:

```bash
./scripts/new-project.sh Acme.Inventory ../Acme.Inventory
cd ../Acme.Inventory
dotnet restore --locked-mode
docker compose up --build -d
```

The .NET template renames solution and project files, namespaces, references, the Data Protection application name, and the user secrets identifier. It copies CI, migrations, tests, operations scripts, and English documentation. It excludes build output, local secrets, and dumps. The source checkout also supports `dotnet new install .` followed by `dotnet new super-backend --name Acme.Inventory`.

For concurrent Compose stacks, give each stack a unique `COMPOSE_PROJECT_NAME` and use a separate set of host ports.

## Account and access APIs

- Public account endpoints under `/api/v1/auth` cover `register`, `confirmEmail`, `resendConfirmationEmail`, `login`, `refresh`, `forgotPassword`, `resetPassword`, and `accept-invitation`. The public registration switch leaves invitation acceptance available.
- An administrator sends an invitation with `POST /api/v1/admin/invitations` and `{"email":"user@example.test"}`. The recipient sends `email`, `code`, and `password` to `POST /api/v1/auth/accept-invitation`. The code expires after two hours and is single-use. If SMTP delivery fails, submitting the same pending invitation resends it.
- Login takes `email` and `password`, plus `twoFactorCode` or `twoFactorRecoveryCode` if needed. Pass the returned access token as `Authorization: Bearer <accessToken>`; send `refreshToken` to renew it. Access and refresh tokens expire after 10 minutes and seven days, respectively. A previously issued refresh token stays valid until it expires or sessions are revoked.
- Use `GET /api/v1/me` and `PUT /api/v1/me` to read or update the profile. The update body contains `displayName`. Use `POST /api/v1/auth/logout-all` to revoke all sessions.
- Use `GET /api/v1/auth/manage/info` for account details and `POST /api/v1/auth/manage/info` for email or password changes. Account changes and `POST /api/v1/auth/manage/2fa` require the `X-Current-Password` header. For TOTP, fetch the shared key with an empty body, sign in again if a new key was generated, then send `enable: true` and a current `twoFactorCode`. Store the returned recovery codes securely.
- Under `/api/v1/admin`, manage roles using `/roles` and `/roles/{roleId}`. Grant or revoke a named permission with PUT or DELETE on `/roles/{roleId}/permissions/{permission}`. Assign or remove the role from a user with PUT or DELETE on `/users/{userId}/roles/{roleId}`. Use `GET /permissions` to list permissions declared by enabled modules.
- Suspend or reactivate a regular account with `PUT /api/v1/admin/users/{id}/status` and `{"disabled":true}` or `{"disabled":false}`. Use `POST /api/v1/admin/users/{id}/revoke-sessions` to leave the account active and revoke its tokens. The built-in administrator role and administrative accounts are protected from these operations.

Send `Accept-Language: fa` for localized API error titles and outgoing email subjects. The response declares its language with `Content-Language`. Framework Identity error codes, audit action identifiers, and the JSON contract are stable across languages.

## Extend and operate

Use `./scripts/new-service.sh InventoryReports` to scaffold and register an optional service with a controller, interface, implementation, and model.

- [Optional OIDC, multitenancy, and durable jobs](docs/OPTIONAL-MODULES.md)
- [Transactional Outbox and consumer deduplication, including Notes](docs/TRANSACTIONAL-OUTBOX.md)
- [Architecture, folder layout, and adding a module](docs/ARCHITECTURE.md)
- [Lifecycle coverage, deployment configuration, databases, and recovery](docs/OPERATIONS.md)
- [ASP.NET Core Identity bearer tokens](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0)

Run the integration suite against local PostgreSQL and Mailpit:

```bash
docker compose up -d --wait database mail
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore
```

The suite creates a temporary database and exercises the HTTP API and SMTP flows. Tests accept `SUPERBACKEND_TEST_DATABASE`, `SUPERBACKEND_TEST_SMTP_HOST`, `SUPERBACKEND_TEST_SMTP_PORT`, and `SUPERBACKEND_TEST_MAILPIT` for alternate fixtures.
