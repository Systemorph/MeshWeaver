using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// HOW a test's mesh is stood up — the ONE thing that differs between the two test bases this repo
/// is retiring.
///
/// <para>🚨 THE MEASUREMENT THAT MOTIVATES IT. <c>MonolithMeshTestBase</c> is 1,742 lines and
/// exactly TWO of them are monolith-specific (<c>UseMonolithMesh</c> + <c>AddInMemoryPersistence</c>).
/// <c>OrleansTestBase</c> is another 425 whose Orleans-specific part is the <c>TestCluster</c> host.
/// Everything else in both — the dev-login admin access, the test partition, the per-test-class
/// compilation-cache isolation, the disposal and leak assertions, the quiesce budgets — is
/// hosting-AGNOSTIC and was maintained twice. Maintainer, 2026-08-30: <i>"one bootstraps orleans
/// with all shenanigans. the other one is simple in-process mesh."</i></para>
///
/// <para><b>A per-SUITE choice, never a per-test one.</b> Measured across core's 44 test projects:
/// 26 require a mesh — 23 bootstrap monolith, 2 bootstrap Orleans, and <b>none use both</b>. So the
/// selection is a class-level parameter, the simplest form this could take.</para>
/// </summary>
public interface IMeshBootstrap
{
    /// <summary>A short name, used in test output and failure text so a verdict says which mesh it
    /// was measured against.</summary>
    string Name { get; }

    /// <summary>Applies the hosting choice to <paramref name="builder"/> and NOTHING else — every
    /// shared registration stays in the base, or the two bootstraps drift apart again.</summary>
    MeshBuilder Bootstrap(MeshBuilder builder);
}

/// <summary>The entry point: <c>MeshBootstrap.Monolith()</c> or <c>MeshBootstrap.Orleans(…)</c>.</summary>
public static class MeshBootstrap
{
    /// <summary>The simple in-process mesh — one host, in-memory persistence, no cluster. What a
    /// test should reach for unless it is specifically about distribution.</summary>
    public static IMeshBootstrap Monolith() => MonolithBootstrap.Instance;

    /// <summary>
    /// An Orleans cluster, configured fluently:
    /// <code>
    /// protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o
    ///     .WithClustering(ClusterProvider.AdoNet, "Server=…")
    ///     .WithGrainStorage(StorageProvider.Redis, "localhost:6379")
    ///     .WithSilos(2));
    /// </code>
    /// Called with no argument it is the localhost, in-memory cluster the current test base builds.
    /// </summary>
    public static IMeshBootstrap Orleans(Action<OrleansBootstrapBuilder>? configure = null)
    {
        var builder = new OrleansBootstrapBuilder();
        configure?.Invoke(builder);
        return new OrleansBootstrap(builder.Build());
    }
}

/// <summary>Where an Orleans cluster keeps its MEMBERSHIP.</summary>
public enum ClusterProvider
{
    /// <summary>Every silo on this machine, membership in memory. The test default, and the only
    /// one that needs no external service.</summary>
    Localhost = 0,

    /// <summary>A relational membership table (SQL Server / PostgreSQL) — what a deployed cluster
    /// uses, and therefore what a test about membership behaviour should use.</summary>
    AdoNet = 1,

    /// <summary>Redis membership.</summary>
    Redis = 2,
}

/// <summary>Where an Orleans cluster keeps its GRAIN STATE.</summary>
public enum StorageProvider
{
    /// <summary>In-memory grain storage: fast, and lost with the silo. The test default.</summary>
    Memory = 0,

    /// <summary>A relational grain store.</summary>
    AdoNet = 1,

    /// <summary>Redis grain storage.</summary>
    Redis = 2,
}

/// <summary>
/// The resolved shape of an Orleans bootstrap — PURE, so the fluent API above is pinned by tests
/// that need no cluster, no Docker and no Orleans reference.
/// </summary>
/// <param name="Clustering">Membership provider.</param>
/// <param name="ClusteringConnectionString">Its connection string; null for <see cref="ClusterProvider.Localhost"/>.</param>
/// <param name="Storage">Grain-storage provider.</param>
/// <param name="StorageConnectionString">Its connection string; null for <see cref="StorageProvider.Memory"/>.</param>
/// <param name="Silos">How many silos the cluster starts with.</param>
public sealed record OrleansBootstrapOptions(
    ClusterProvider Clustering = ClusterProvider.Localhost,
    string? ClusteringConnectionString = null,
    StorageProvider Storage = StorageProvider.Memory,
    string? StorageConnectionString = null,
    short Silos = 1)
{
    /// <summary>
    /// Why this shape cannot be stood up, or null when it can. Pure — the fluent API is checked
    /// where it is WRITTEN rather than where a silo fails to start, because an Orleans cluster that
    /// cannot reach its membership table fails minutes later with a message about a connection,
    /// not about the test that asked for it.
    /// </summary>
    public string? Problem()
    {
        if (Silos < 1)
            return $"a cluster needs at least one silo; {Silos} was requested.";
        if (Clustering != ClusterProvider.Localhost && string.IsNullOrWhiteSpace(ClusteringConnectionString))
            return $"{Clustering} clustering needs a connection string — only "
                + $"{ClusterProvider.Localhost} membership is self-contained.";
        if (Clustering == ClusterProvider.Localhost && !string.IsNullOrWhiteSpace(ClusteringConnectionString))
            return $"{ClusterProvider.Localhost} clustering takes no connection string, and one was "
                + "given — it would be silently ignored, which is how a test believes it is "
                + "exercising a real membership table and is not.";
        if (Storage != StorageProvider.Memory && string.IsNullOrWhiteSpace(StorageConnectionString))
            return $"{Storage} grain storage needs a connection string — only "
                + $"{StorageProvider.Memory} storage is self-contained.";
        if (Storage == StorageProvider.Memory && !string.IsNullOrWhiteSpace(StorageConnectionString))
            return $"{StorageProvider.Memory} grain storage takes no connection string, and one was given.";
        return null;
    }

    /// <summary>The one-line description a test verdict carries. Pure.</summary>
    public string Describe() =>
        $"orleans[{Clustering.ToString().ToLowerInvariant()}/"
        + $"{Storage.ToString().ToLowerInvariant()}, {Silos} silo{(Silos == 1 ? "" : "s")}]";
}

/// <summary>The fluent builder behind <see cref="MeshBootstrap.Orleans"/>.</summary>
public sealed class OrleansBootstrapBuilder
{
    private OrleansBootstrapOptions options = new();

    /// <summary>Membership provider, and its connection string for everything but
    /// <see cref="ClusterProvider.Localhost"/>.</summary>
    public OrleansBootstrapBuilder WithClustering(ClusterProvider provider, string? connectionString = null)
    {
        options = options with { Clustering = provider, ClusteringConnectionString = connectionString };
        return this;
    }

    /// <summary>Grain-storage provider, and its connection string for everything but
    /// <see cref="StorageProvider.Memory"/>.</summary>
    public OrleansBootstrapBuilder WithGrainStorage(StorageProvider provider, string? connectionString = null)
    {
        options = options with { Storage = provider, StorageConnectionString = connectionString };
        return this;
    }

    /// <summary>How many silos to start. More than one is what makes a cross-silo test a
    /// cross-silo test.</summary>
    public OrleansBootstrapBuilder WithSilos(short count)
    {
        options = options with { Silos = count };
        return this;
    }

    /// <summary>The resolved options — REFUSING an unusable shape here, where the test wrote it.</summary>
    public OrleansBootstrapOptions Build()
    {
        if (options.Problem() is { } problem)
            throw new ArgumentException($"This Orleans bootstrap cannot be stood up: {problem}");
        return options;
    }
}

/// <summary>The simple in-process mesh.</summary>
public sealed class MonolithBootstrap : IMeshBootstrap
{
    /// <summary>The shared instance — the bootstrap holds no state.</summary>
    public static readonly MonolithBootstrap Instance = new();

    /// <inheritdoc />
    public string Name => "monolith";

    /// <inheritdoc />
    public MeshBuilder Bootstrap(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence();
}

/// <summary>
/// An Orleans cluster, described here and STOOD UP by the Orleans-referencing assembly.
///
/// <para>🚨 The split is deliberate: this project must not take an Orleans dependency, or the 23
/// monolith suites would drag a cluster's worth of packages to run an in-process mesh. So the
/// fluent API and its validation live here — pure and testable without Orleans — and the applicator
/// is registered by whoever owns the cluster. Until one is, this says so by name rather than
/// failing minutes later inside a silo.</para>
/// </summary>
public sealed class OrleansBootstrap(OrleansBootstrapOptions options) : IMeshBootstrap
{
    /// <summary>The applicator the Orleans test assembly registers — it turns the described cluster
    /// into a configured builder. Static because the choice is per-process, not per-suite.</summary>
    public static Func<OrleansBootstrapOptions, MeshBuilder, MeshBuilder>? Applicator { get; set; }

    /// <summary>The resolved cluster shape.</summary>
    public OrleansBootstrapOptions Options => options;

    /// <inheritdoc />
    public string Name => options.Describe();

    /// <inheritdoc />
    public MeshBuilder Bootstrap(MeshBuilder builder)
        => Applicator is { } apply
            ? apply(options, builder)
            : throw new InvalidOperationException(
                $"No Orleans applicator is registered, so {options.Describe()} cannot be stood up. "
                + "The assembly that references Orleans sets OrleansBootstrap.Applicator; a suite "
                + "asking for an Orleans mesh must run where that assembly is loaded.");
}
