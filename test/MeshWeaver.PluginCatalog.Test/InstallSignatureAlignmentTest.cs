#pragma warning disable CS1591

using System;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the MATERIALIZED-DEFAULT alignment in the installer's unchanged-check
/// (<see cref="PackageInstaller.IsUnchanged"/> → <c>AlignedIncoming</c>): the persisted side of a
/// re-install compare is often TYPED — the owning hub re-serialized it, materializing C# property
/// defaults (<c>PluginCatalogContent.SourceRef = "HEAD"</c> here; <c>PluginContent.Currency =
/// "CHF"</c> in the diagnosed plugins-gate case) — while the incoming side is the repo file's raw
/// <c>JsonElement</c>, which legitimately omits defaulted properties. Without alignment every
/// materialized default reads as a change and the root rewrites on every re-install ONCE the hub
/// happens to have re-serialized it: the nondeterministic "re-install wrote 1 node(s)" idempotence
/// flap behind ~14 allow-listed packages in the plugins repo's gate.
/// </summary>
public class InstallSignatureAlignmentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private MeshNode Node(object content) => new("cat", "Store")
    {
        NodeType = "PluginCatalog",
        Name = "Catalog",
        State = MeshNodeState.Active,
        Content = content,
    };

    private JsonElement Element(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json, Mesh.JsonSerializerOptions);

    /// <summary>The diagnosed churn shape: typed current with a materialized default vs a repo file
    /// omitting that property — MUST read as unchanged.</summary>
    [Fact(Timeout = 30000)]
    public void TypedCurrentWithMaterializedDefault_VsFileOmittingIt_IsUnchanged()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });
        // The repo file's shape: $type + the authored property only — no sourceRef, which the
        // typed side materializes as "HEAD".
        var incoming = Node(Element("""{"$type":"PluginCatalogContent","sourceRepoPath":"/repo"}"""));

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeTrue(
                "a property the file omits and the type defaults is NOT a change — comparing the " +
                "raw element against the typed content is the root-churn idempotence flap");
    }

    /// <summary>An authored value differing from the persisted one is still a change.</summary>
    [Fact(Timeout = 30000)]
    public void RealValueChange_IsStillDetected()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });
        var incoming = Node(Element(
            """{"$type":"PluginCatalogContent","sourceRepoPath":"/repo","sourceRef":"v2"}"""));

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse("sourceRef HEAD → v2 is a real change the alignment must not mask");
    }

    /// <summary>A $type change must never be masked by coercing the element into the wrong type.</summary>
    [Fact(Timeout = 30000)]
    public void ContentTypeChange_IsStillDetected()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });
        var incoming = Node(Element("""{"$type":"SomeOtherContent","sourceRepoPath":"/repo"}"""));

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse("a differing $type IS a real change — alignment only applies same-type");
    }
}
