using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Security.Cryptography;
using SuperBackend.Identity;
using SuperBackend.Notes;
using Xunit;

namespace SuperBackend.IntegrationTests;

public sealed class BackendDatabase : IAsyncLifetime
{
    private static readonly string LocalConnection =
        "Host=localhost;Port=54329;Database=superbackend;Username=superbackend;Password=local-dev-only;GSS Encryption Mode=Disable";
    private readonly string _name = $"super_backend_test_{Guid.NewGuid():N}";
    private readonly string _server = Environment.GetEnvironmentVariable("SUPERBACKEND_TEST_DATABASE") ?? LocalConnection;

    private readonly string _tenantRole = $"tenant_runtime_{Guid.NewGuid():N}";

    internal string DatabaseConnection =>
        new NpgsqlConnectionStringBuilder(_server) { Database = _name }.ConnectionString;

    internal async Task<string> CreateTenantRuntimeRoleAsync()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE ROLE "{_tenantRole}" LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{password}';
            GRANT USAGE ON SCHEMA tenancy TO "{_tenantRole}";
            GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy."Documents" TO "{_tenantRole}";
            """, connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(DatabaseConnection)
        {
            Username = _tenantRole,
            Password = password
        }.ConnectionString;
    }

    internal WebApplicationFactory<Program> CreateApp(Dictionary<string, string?>? overrides = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = DatabaseConnection,
            ["RateLimits:RequestsPerMinute"] = "500",
            ["RateLimits:IdentityRequestsPerMinute"] = "200",
            ["Smtp:Host"] = Environment.GetEnvironmentVariable("SUPERBACKEND_TEST_SMTP_HOST") ?? "localhost",
            ["Smtp:Port"] = Environment.GetEnvironmentVariable("SUPERBACKEND_TEST_SMTP_PORT") ?? "10250"
        };
        foreach (var (key, value) in overrides ?? [])
            configuration[key] = value;

        return new ConfiguredApplication(configuration);
    }

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""CREATE DATABASE "{_name}" """, connection);
        await command.ExecuteNonQueryAsync();

        await using var app = CreateApp();
        await app.Services.MigrateIdentityAsync();
        await app.Services.MigrateNotesAsync();
        await app.Services.BootstrapAdministratorAsync(BackendTests.AdminEmail, BackendTests.Password);
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""DROP DATABASE IF EXISTS "{_name}" WITH (FORCE)""", connection);
        await command.ExecuteNonQueryAsync();
        await using var role = new NpgsqlCommand($"""DROP ROLE IF EXISTS "{_tenantRole}" """, connection);
        await role.ExecuteNonQueryAsync();
    }

    private sealed class ConfiguredApplication(Dictionary<string, string?> configuration) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(configuration));
            return base.CreateHost(builder);
        }
    }
}
