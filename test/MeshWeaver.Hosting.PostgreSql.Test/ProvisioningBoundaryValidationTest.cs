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

    private static PostgreSqlPartitionStorageProvider CreateProvider()
        => new(
            NpgsqlDataSource.Create(NeverConnectedConnectionString),
            NeverConnectedConnectionString,
            new PostgreSqlStorageOptions { ConnectionString = NeverConnectedConnectionString });

    [Theory]
    [InlineData("search?q=query%20syntax&hq=scope%3adescendants")]  // the literal #714 junk schemas
    [InlineData("login?returnurl=https%3a%2f%2fmemex.systemorph.com%2fauthorize")]
    [InlineData("a b")]
    [InlineData("ns:with:colons")]
    [InlineData("_access")]
    public async Task EnsurePartitionProvisioned_ErrorsLoud_OnMalformedId(string junk)
    {
        var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.EnsurePartitionProvisioned(junk)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken));

        ex.Message.Should().Contain(junk,
            "the rejection must NAME the offending id so the caller can see what was refused");
        ex.Message.Should().Contain(PartitionDefinition.PartitionSegmentRequirement,
            "the rejection must say WHY the id was refused");
    }

    [Fact]
    public async Task EnsurePartitionProvisioned_TooLongId_ErrorsLoud()
    {
        var provider = CreateProvider();
        var tooLong = new string('a', 64);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.EnsurePartitionProvisioned(tooLong)
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
        var provider = CreateProvider();
        var def = new PartitionDefinition
        {
            Namespace = "search?q=agent&hq=scope%3adescendants",
            Schema = "search?q=agent&hq=scope%3adescendants",
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.EnsureSchemaForPartitionAsync(def, TestContext.Current.CancellationToken));

        ex.Message.Should().Contain("search?q=agent&hq=scope%3adescendants");
        ex.Message.Should().Contain(PartitionDefinition.PartitionSegmentRequirement);
    }
}
