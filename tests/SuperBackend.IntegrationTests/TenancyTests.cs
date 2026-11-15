using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SuperBackend.Tenancy;
using Xunit;
using static SuperBackend.IntegrationTests.BackendTests;

namespace SuperBackend.IntegrationTests;

public sealed class TenancyTests(BackendDatabase database) : IClassFixture<BackendDatabase>
{
    private const string Tenants = "/api/v1/tenants";

    [Fact]
    public async Task TenantMembershipAndPostgresPoliciesBothEnforceIsolation()
    {
        var settings = new Dictionary<string, string?> { ["Modules:Tenancy"] = "true" };
        await using (var migration = database.CreateApp(settings))
            await migration.Services.MigrateTenancyAsync();

        var runtime = await database.CreateTenantRuntimeRoleAsync();
        settings["ConnectionStrings:TenantData"] = runtime;
        await using var app = database.CreateApp(settings);
        using var admin = CreateClient(app);
        using var a = CreateClient(app);
        using var b = CreateClient(app);
        await ExpectAsync(admin.GetAsync("/health/ready"), HttpStatusCode.OK);
        await AuthenticateAsync(admin, AdminEmail);
        await AuthenticateAsync(a, await CreateAccountAsync(a));
        await AuthenticateAsync(b, await CreateAccountAsync(b));
        var userA = await ReadAsync<UserView>(a.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        var userB = await ReadAsync<UserView>(b.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        await ExpectAsync(a.PostAsJsonAsync(Tenants, new { name = "Unauthorized" }), HttpStatusCode.Forbidden);

        var tenantA = await ReadAsync<TenantSummary>(admin.PostAsJsonAsync(Tenants, new { name = "A" }), HttpStatusCode.Created);
        var tenantB = await ReadAsync<TenantSummary>(admin.PostAsJsonAsync(Tenants, new { name = "B" }), HttpStatusCode.Created);
        await ExpectAsync(admin.PutAsJsonAsync(Membership(tenantA.Id, userA.Id), new { access = "Editor" }), HttpStatusCode.NoContent);
        await ExpectAsync(admin.PutAsJsonAsync(Membership(tenantB.Id, userB.Id), new { access = "Editor" }), HttpStatusCode.NoContent);

        var docA = await ReadAsync<TenantDocument>(
            a.PostAsJsonAsync(Documents(tenantA.Id), new DocumentInput("Private A", "")), HttpStatusCode.Created);
        var docB = await ReadAsync<TenantDocument>(
            b.PostAsJsonAsync(Documents(tenantB.Id), new DocumentInput("Private B", "")), HttpStatusCode.Created);
        await ExpectAsync(a.GetAsync(Documents(tenantB.Id)), HttpStatusCode.NotFound);
        await ExpectAsync(admin.GetAsync(Documents(tenantA.Id)), HttpStatusCode.NotFound);
        await ExpectAsync(a.GetAsync(Documents(tenantA.Id) + "/" + docB.Id), HttpStatusCode.NotFound);
        Assert.Equal(docA, Assert.Single(await ReadAsync<TenantDocument[]>(a.GetAsync(Documents(tenantA.Id)), HttpStatusCode.OK)));
        Assert.Equal(tenantA, Assert.Single(await ReadAsync<TenantSummary[]>(a.GetAsync(Tenants), HttpStatusCode.OK)));

        await ExpectAsync(admin.PutAsJsonAsync(Membership(tenantA.Id, userB.Id), new { access = "Reader" }), HttpStatusCode.NoContent);
        await ExpectAsync(b.GetAsync(Documents(tenantA.Id) + "/" + docA.Id), HttpStatusCode.OK);
        await ExpectAsync(b.PutAsJsonAsync(Documents(tenantA.Id) + "/" + docA.Id, new DocumentInput("Wrong", "")), HttpStatusCode.NotFound);
        await ExpectAsync(admin.DeleteAsync(Membership(tenantA.Id, userB.Id)), HttpStatusCode.NoContent);
        await ExpectAsync(b.GetAsync(Documents(tenantA.Id) + "/" + docA.Id), HttpStatusCode.NotFound);

        await AssertDatabasePolicyAsync(runtime, tenantA.Id, tenantB.Id, docB.Id);

        settings.Remove("ConnectionStrings:TenantData");
        await using var unsafeConfiguration = database.CreateApp(settings);
        using var unsafeClient = CreateClient(unsafeConfiguration);
        await ExpectAsync(unsafeClient.GetAsync("/health/ready"), HttpStatusCode.ServiceUnavailable);
        await AuthenticateAsync(unsafeClient, AdminEmail);
        var operatorId = (await ReadAsync<UserView>(unsafeClient.GetAsync("/api/v1/me"), HttpStatusCode.OK)).Id;
        await ExpectAsync(admin.PutAsJsonAsync(Membership(tenantA.Id, operatorId), new { access = "Editor" }), HttpStatusCode.NoContent);
        await ExpectAsync(unsafeClient.GetAsync(Documents(tenantA.Id)), HttpStatusCode.InternalServerError);
    }

    private static async Task AssertDatabasePolicyAsync(string runtime, Guid tenantA, Guid tenantB, Guid docB)
    {
        await using var connection = new NpgsqlConnection(runtime);
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using var set = new NpgsqlCommand("SELECT set_config('superbackend.tenant_id', @tenant, true)", connection, tx);
            set.Parameters.AddWithValue("tenant", tenantB.ToString());
            await set.ExecuteScalarAsync();
            // Intentionally omit any tenant predicate to exercise the PostgreSQL policy independently.
            await using var rows = new NpgsqlCommand("""SELECT "Id" FROM tenancy."Documents" """, connection, tx);
            Assert.Equal(docB, Assert.IsType<Guid>(await rows.ExecuteScalarAsync()));
            await using var insert = new NpgsqlCommand(
                """INSERT INTO tenancy."Documents" ("TenantId", "Id", "Title", "Body") VALUES (@tenant, @id, 'x', '')""",
                connection, tx);
            insert.Parameters.AddWithValue("tenant", tenantA);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(async () => await insert.ExecuteNonQueryAsync())).SqlState);
            await tx.RollbackAsync();
        }

        await using var unscoped = new NpgsqlCommand("""SELECT count(*) FROM tenancy."Documents" """, connection);
        Assert.Equal(0L, Assert.IsType<long>(await unscoped.ExecuteScalarAsync()));
    }

    private static string Documents(Guid tenantId) => $"{Tenants}/{tenantId}/documents";
    private static string Membership(Guid tenantId, string userId) => $"{Tenants}/{tenantId}/members/{userId}";
    private sealed record UserView(string Id);
}
