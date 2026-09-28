using Microsoft.EntityFrameworkCore;
using ServiceScheduler.Data;
using ServiceScheduler.Shared;

namespace ServiceScheduler.Tests;

/// <summary>
/// Creates a throwaway database on the local PostgreSQL instance and runs the real
/// migrations against it. These tests are deliberately not mocked: the invariant under
/// test is a database constraint, so replacing the database would test nothing. EF Core's
/// InMemory provider enforces no constraints, and SQLite has no gist or EXCLUDE.
/// </summary>
public sealed class SchedulerFixture : IAsyncLifetime
{
    /// <summary>
    /// Base connection string, without a database name. Supply it through
    /// <c>SCHEDULER_TEST_DB</c> so no credential is committed; see README.
    /// </summary>
    private static readonly string BaseConnection =
        Environment.GetEnvironmentVariable("SCHEDULER_TEST_DB")
        ?? "Host=localhost;Port=5432;Username=postgres";

    private static string AdminConnection => $"{BaseConnection};Database=postgres";

    private readonly string _database = $"servicescheduler_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await ExecuteOnAdminAsync($"CREATE DATABASE \"{_database}\";");

        ConnectionString = $"{BaseConnection};Database={_database}";

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await SeedData.EnsureSeededAsync(db);
    }

    public async Task DisposeAsync()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        await ExecuteOnAdminAsync($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);");
    }

    public SchedulerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SchedulerDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new SchedulerDbContext(options);
    }

    private static async Task ExecuteOnAdminAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(AdminConnection);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Fixed caller identity, standing in for authentication.</summary>
public sealed class TestServiceContext(Guid customerId) : IServiceContext
{
    public Guid CustomerId { get; } = customerId;

    public string CorrelationId { get; } = "test";
}

[CollectionDefinition(nameof(SchedulerCollection))]
public sealed class SchedulerCollection : ICollectionFixture<SchedulerFixture>;
