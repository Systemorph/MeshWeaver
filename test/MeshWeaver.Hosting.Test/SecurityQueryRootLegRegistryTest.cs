using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins <see cref="QueryRouteClassifier.RegisteredGlobalSatellites"/> — the mirror the pure
/// shapes test classifies <c>_</c>-prefixed first segments with — against the REAL registry of a
/// running mesh: the <c>Partition</c> nodes the static providers ship, which is what the Postgres
/// router's <c>TryGetRegisteredPartition</c> is seeded from at boot. A mirror that nothing compares
/// to the thing it mirrors is a hypothesis (#2194 was one: the census called the root
/// <c>_Access</c> leg a fan-out for a year on the strength of a comment).
///
/// <para>Two facts carry the root legs' routing verdicts:</para>
/// <list type="bullet">
///   <item><c>_Access</c> IS registered, with schema <c>system_access</c> — so
///     <see cref="SecurityQueries.RootAssignments"/> is served by ONE schema.</item>
///   <item><c>_Policy</c> is NOT registered — so <see cref="SecurityQueries.RootPolicy"/> is
///     unroutable rather than pinned, and (the same fact from the write side) no root
///     <c>_Policy</c> row can exist on Postgres, which is why the path-less spelling's 199-schema
///     fan-out was always empty. Register it and BOTH tests here tell you which assertions move.</item>
/// </list>
/// </summary>
public class SecurityQueryRootLegRegistryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private Dictionary<string, string?> RegisteredGlobalSatellites()
    {
        var options = Mesh.JsonSerializerOptions;
        return Mesh.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .Where(n => string.Equals(n.NodeType, PartitionNodeType.NodeType, StringComparison.Ordinal))
            .Select(n => n.ContentAs<PartitionDefinition>(options))
            .Where(d => d is { Namespace.Length: > 0 } && d.Namespace.StartsWith('_'))
            .ToDictionary(d => d!.Namespace, d => d!.Schema, StringComparer.Ordinal);
    }

    [Fact]
    public void TheShapesTestMirrorMatchesTheRealRegistry()
    {
        var real = RegisteredGlobalSatellites();
        real.Should().NotBeEmpty("DefaultPartitionProvider registers at least _Access");
        var mirror = QueryRouteClassifier.RegisteredGlobalSatellites;
        real.Keys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
            mirror.Keys.OrderBy(k => k, StringComparer.Ordinal),
            "the shapes test classifies `_`-prefixed first segments with this mirror; when a global "
            + "satellite is registered or removed, update the mirror in the same change");
        foreach (var (ns, schema) in mirror)
            real[ns].Should().Be(schema, $"the mirror's schema for {ns} must be the registered one");
    }

    [Fact]
    public void TheRootAccessNamespaceIsRegistered_AndTheRootPolicyPathIsNot()
    {
        var real = RegisteredGlobalSatellites();
        real.Should().ContainKey(SecurityQueries.RootAccessNamespace)
            .WhoseValue.Should().Be("system_access",
                "the root grants leg is served by this one schema, never by the cross-schema fan-out");
        real.Should().NotContainKey(SecurityQueries.RootPolicyPath,
            "no schema is registered for the root policy, so the read is unroutable (answered "
            + "empty without a fan-out) and no write can land a root _Policy on Postgres — if this "
            + "fails, SecurityQueryShapesTest.TheRootPolicyLegNeverFansOut moves to Pinned");
    }
}
