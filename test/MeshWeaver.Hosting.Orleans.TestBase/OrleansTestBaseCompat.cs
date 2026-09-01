using System;
using MeshWeaver.Hosting.Monolith.TestBase;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

// 🚨 DELETE THIS FILE — it is a cross-repo BRIDGE, not a design.
//
// `OrleansTestBase<T>`, `OrleansTestBase` and `OrleansSharedTestBase` were three names for one
// piece of machinery written twice; that machinery is now `OrleansMeshTestBase` + the fixture, and
// every suite in THIS repo derives from it directly (grep: no core file names any of the three).
// The names survive only because MeshWeaver.Plugins still derives from them in 11 places, and its
// CI checks this repository out at MAIN, unpinned (`.github/workflows/ci.yml`) — so deleting them
// here reds that trunk the moment this merges, before its own conversion can land.
//
// The order, and the only order that has no red window:
//   1. this PR                — OrleansMeshTestBase lands; core stops using the old names.
//   2. MeshWeaver.Plugins PR  — its 10 `OrleansTestBase<T>` suites and `AiOrleansSharedTestBase`
//                               move to OrleansMeshTestBase.
//   3. a two-line PR here     — this file goes.
//
// Nothing may be ADDED to this file. A new suite derives from OrleansMeshTestBase.

/// <summary>
/// OBSOLETE bridge — derive from <see cref="OrleansMeshTestBase"/> and override
/// <see cref="OrleansMeshTestBase.SiloConfiguratorType"/> instead. See the file header.
/// </summary>
/// <typeparam name="TSiloConfigurator">The silo configurator, now a VALUE rather than a type
/// argument: Orleans <c>new()</c>s it, so the generic parameter only ever propagated itself into
/// every signature that mentioned the base.</typeparam>
public abstract class OrleansTestBase<TSiloConfigurator>(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
    where TSiloConfigurator : ISiloConfigurator, IHostConfigurator, new()
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(TSiloConfigurator);

    /// <summary>
    /// OBSOLETE knob — say <c>Bootstrap => MeshBootstrap.Orleans(o =&gt; o.WithSilos(n))</c> instead.
    /// Kept because MeshWeaver.Plugins still overrides it (<c>OrleansPostgresLifecycleTest</c>), and
    /// removing it would break that build before its own conversion can land.
    /// </summary>
    protected virtual short InitialSilosCount => 1;

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o.WithSilos(InitialSilosCount));
}

/// <summary>
/// OBSOLETE bridge — derive from <see cref="OrleansMeshTestBase"/> and override
/// <see cref="OrleansMeshTestBase.SiloConfiguratorType"/> to <see cref="TestSiloConfigurator"/>.
/// See the file header.
/// </summary>
public abstract class OrleansTestBase(ITestOutputHelper output)
    : OrleansTestBase<TestSiloConfigurator>(output);

/// <summary>
/// OBSOLETE bridge — derive from <see cref="OrleansMeshTestBase"/>, whose defaults ARE this class's
/// behaviour (a pooled cluster on the stock silo configurator). See the file header.
/// </summary>
public abstract class OrleansSharedTestBase(ITestOutputHelper output) : OrleansMeshTestBase(output);
