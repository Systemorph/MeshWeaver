using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// #714 defense-in-depth guard at the provisioning BOUNDARY itself. The router refusing to
/// ROUTE a malformed segment is not enough — a deliberate top-level create (or any future
/// caller) must not be able to reach <c>public.ensure_partition_schema</c> with a
/// URL-shaped id either. These tests exercise the REAL
/// <see cref="PostgreSqlPartitionStorageProvider"/> seams
/// (<see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/> and the
/// <c>EnsureSchemaAsync</c> funnel via <c>EnsureSchemaForPartitionAsync</c>) and assert the
/// rejection is LOUD — an <see cref="ArgumentException"/> naming the offending id through
/// the observable's OnError — never a silent no-op the caller mistakes for success.
/// The guard fires before any DB round-trip, so no container is needed: the data source
/// below is never connected.
/// </summary>
public class ProvisioningBoundaryValidationTest
{
    private const string NeverConnectedConnectionString =
        "Host=localhost;Port=1;Database=never;Username=never;Password=never";

    /// <summary>
    /// Provider + the data source it was built over, disposed together. The provider's own
    /// <c>Dispose</c> does not own the injected data source, so a bare <c>CreateProvider()</c>
    /// would leak one pooled <see cref="NpgsqlDataSource"/> per test case.
    /// </summary>
    private sealed record ProviderScope(
        PostgreSqlPartitionStorageProvider Provider, NpgsqlDataSource DataSource) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            DataSource.Dispose();
        }
    }

    private static ProviderScope CreateProvider()
    {
        var dataSource = NpgsqlDataSource.Create(NeverConnectedConnectionString);
        return new ProviderScope(
            new PostgreSqlPartitionStorageProvider(
                dataSource,
                NeverConnectedConnectionString,
                new PostgreSqlStorageOptions { ConnectionString = NeverConnectedConnectionString }),
            dataSource);
    }

    [Theory]
    [InlineData("search?q=query%20syntax&hq=scope%3adescendants")]  // the literal #714 junk schemas
    [InlineData("login?returnurl=https%3a%2f%2fmemex.systemorph.com%2fauthorize")]
    [InlineData("a b")]
    [InlineData("ns:with:colons")]
    // 🚨 `_access` is junk AS A PARTITION NAME, and must stay junk. A `_`-prefixed name is a
    // satellite CONTAINER segment ({P}/_Access/…) or a global-satellite NAMESPACE whose schema
    // comes from a REGISTERED PartitionDefinition (`_Access` → `system_access`) — the router
    // never derives a schema from a `_`-prefixed segment, so a schema named `_access` could
    // never be routed to. Exempting `_`-prefixed names here (as an earlier revision of this PR
    // did) re-opens exactly the ghost-schema hole #714 is about; the real defect that motivated
    // the exemption was a CALLER passing the root-scope `_Access` grant's namespace as a
    // partition — fixed in MeshExtensions.EnsurePartitionBootstrap.
    [InlineData("_access")]
    public async Task EnsurePartitionProvisioned_ErrorsLoud_OnMalformedId(string junk)
    {
        using var scope = CreateProvider();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Provider.EnsurePartitionProvisioned(junk)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken));

        ex.Message.Should().Contain(junk,
            "the rejection must NAME the offending id so the caller can see what was refused");
        ex.Message.Should().Contain(PartitionDefinition.PartitionSegmentRequirement,
            "the rejection must say WHY the id was refused");
    }

    [Theory]
    [InlineData(64, 'a')]   // 64 ASCII chars = 64 bytes
    // 32 two-byte chars = 64 UTF-8 BYTES but only 32 chars: Postgres' NAMEDATALEN cap is a BYTE
    // cap that silently TRUNCATES, so a char-counted rule would provision this under a truncated
    // schema name the router (computing the untruncated name) could never route back to.
    [InlineData(32, 'ü')]
    public async Task EnsurePartitionProvisioned_TooLongId_ErrorsLoud(int count, char fill)
    {
        using var scope = CreateProvider();
        var tooLong = new string(fill, count);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Provider.EnsurePartitionProvisioned(tooLong)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken));

        ex.Message.Should().Contain(tooLong);
    }

    [Fact]
    public async Task EnsureSchemaFunnel_ErrorsLoud_OnMalformedSchema()
    {
        // The EnsureSchemaAsync funnel (also fed by the boot-time static-partition seeding
        // via EnsureSchemaForPartitionAsync) must apply the SAME rule to the resolved schema
        // name — no code path may pass a junk name into public.ensure_partition_schema.
        using var scope = CreateProvider();
        var def = new PartitionDefinition
        {
            Namespace = "search?q=agent&hq=scope%3adescendants",
            Schema = "search?q=agent&hq=scope%3adescendants",
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Provider.EnsureSchemaForPartitionAsync(def, TestContext.Current.CancellationToken));

        ex.Message.Should().Contain("search?q=agent&hq=scope%3adescendants");
        ex.Message.Should().Contain(PartitionDefinition.PartitionSegmentRequirement);
    }
}
