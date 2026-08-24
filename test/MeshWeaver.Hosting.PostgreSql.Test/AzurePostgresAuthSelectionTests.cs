using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins <see cref="AzurePostgres"/> — the ONE rule that decides whether a Postgres connection
/// authenticates with an Entra-ID token or a password. These are pure (no container, no fixture),
/// so they run in the unit lane, not the <c>[Collection("PostgreSql")]</c> integration lane.
///
/// <para>The regression this guards is a production SIGABRT: selecting the Azure token provider by
/// a substring match on the whole connection string wired <c>UsePeriodicPasswordProvider</c> onto
/// a data source that ALSO carried a password (a fully-qualified Azure host reached with
/// username+password). Npgsql then threw
/// <c>NotSupportedException: "When registering a password provider, a password or password file
/// may not be set"</c> on the first connect, faulting <c>PostgreSqlChangeListener</c> and aborting
/// the portal. So the load-bearing case is
/// <see cref="AzureHostWithPassword_DoesNotUseManagedIdentity"/>.</para>
/// </summary>
public class AzurePostgresAuthSelectionTests
{
    private const string AzureHost = "memexaks-pg.postgres.database.azure.com"; // local-only-guard:allow — parse-only fixture, never connects

    // ---- IsAzureHost: a HOST test, never a whole-string substring match --------------------

    [Theory]
    [InlineData("Host=memexaks-pg.postgres.database.azure.com;Database=memex;Username=app")] // local-only-guard:allow
    [InlineData("Host=memexaks-pg.postgres.database.azure.com;Database=memex;Username=app;Password=secret")] // local-only-guard:allow
    [InlineData("Host=MEMEXAKS-PG.POSTGRES.DATABASE.AZURE.COM;Database=memex")] // case-insensitive suffix; local-only-guard:allow
    public void AzureHost_IsRecognised(string connectionString)
        => Assert.True(AzurePostgres.IsAzureHost(connectionString));

    [Theory]
    [InlineData("Host=localhost;Database=memex;Username=postgres;Password=postgres")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=memex")]
    [InlineData("Host=pgvector;Database=memex;Username=app;Password=app")] // Compose/Helm container
    public void NonAzureHost_IsNotAzure(string connectionString)
        => Assert.False(AzurePostgres.IsAzureHost(connectionString));

    [Fact]
    public void SuffixInPasswordOrDatabase_DoesNotFalseMatch()
    {
        // The suffix appears in the DATABASE and PASSWORD but the HOST is local — the substring
        // bug matched this; the host test must not.
        const string cs = "Host=localhost;Database=database.azure.com;Username=app;Password=pw.postgres.database.azure.com"; // local-only-guard:allow — suffix in DB/password, host is local
        Assert.False(AzurePostgres.IsAzureHost(cs));
        Assert.False(AzurePostgres.UsesManagedIdentityAuth(cs));
    }

    // ---- UsesManagedIdentityAuth: Azure host AND no password -------------------------------

    [Fact]
    public void AzureHostWithoutPassword_UsesManagedIdentity()
    {
        var cs = $"Host={AzureHost};Database=memex;Username=db_migration_identity";
        Assert.True(AzurePostgres.IsAzureHost(cs));
        Assert.True(AzurePostgres.UsesManagedIdentityAuth(cs));
    }

    [Fact] // THE regression guard: Azure host + password must NOT take the token path.
    public void AzureHostWithPassword_DoesNotUseManagedIdentity()
    {
        var cs = $"Host={AzureHost};Database=memex;Username=app;Password=super-secret";
        Assert.True(AzurePostgres.IsAzureHost(cs));          // still an Azure host …
        Assert.False(AzurePostgres.UsesManagedIdentityAuth(cs)); // … but the plain (password) path
    }

    [Fact]
    public void AzureHostWithEmptyPassword_UsesManagedIdentity()
    {
        // An explicit empty password is still "no password" — the token path applies.
        var cs = $"Host={AzureHost};Database=memex;Username=app;Password=";
        Assert.True(AzurePostgres.UsesManagedIdentityAuth(cs));
    }

    [Fact]
    public void NonAzureHostWithoutPassword_DoesNotUseManagedIdentity()
    {
        const string cs = "Host=localhost;Database=memex;Username=postgres";
        Assert.False(AzurePostgres.UsesManagedIdentityAuth(cs));
    }

    // ---- Degenerate inputs: null / empty / unparseable → not Azure (fall to plain path) -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a connection string ===")]
    public void NullEmptyOrUnparseable_IsNotAzureAndNotManagedIdentity(string? connectionString)
    {
        Assert.False(AzurePostgres.IsAzureHost(connectionString));
        Assert.False(AzurePostgres.UsesManagedIdentityAuth(connectionString));
    }
}
