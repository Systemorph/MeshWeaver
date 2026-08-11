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

    /// <summary>
    /// A property the file ADDS but the current type lacks must read as a change: with the ambient
    /// Skip unmapped-member handling, the alignment deserialize would silently drop it and mask a
    /// real difference — the strict Disallow clone routes this to the raw compare instead.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UnknownIncomingProperty_IsStillDetected()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });
        var incoming = Node(Element(
            """{"$type":"PluginCatalogContent","sourceRepoPath":"/repo","brandNewProperty":"x"}"""));

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse(
                "an unknown incoming member means the file carries something the persisted shape " +
                "does not — alignment must fall back to the raw compare, never silently drop it");
    }

    /// <summary>#727 defect 2: a change to <c>Order</c> ALONE must read as a change — the installer
    /// skipped order-only updates because <c>ScalarsUnchanged</c> did not compare Order.</summary>
    [Fact(Timeout = 30000)]
    public void OrderOnlyChange_IsDetected()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" }) with { Order = 10 };
        var incoming = current with { Order = 20 };

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse("an order-only change must be written, not skipped as unchanged");
    }

    /// <summary>Control: identical nodes (same Order) still read as unchanged — the fix must not
    /// over-trigger a rewrite.</summary>
    [Fact(Timeout = 30000)]
    public void SameOrder_IsStillUnchanged()
    {
        var current = Node(new PluginCatalogContent { SourceRepoPath = "/repo" }) with { Order = 10 };
        var incoming = current with { };

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeTrue("nodes identical in every applied field (Order included) stay unchanged");
    }

    /// <summary>
    /// 🚨 The MIRROR of the diagnosed churn — the flap that SURVIVED the one-sided alignment
    /// (FractalStars/Stars, 2026-08-11): the persisted side was read BEFORE the module's own type
    /// registration resolved, so it is a raw <c>JsonElement</c> omitting defaulted properties,
    /// while the incoming repo file deserialized TYPED and materializes them. Same values → MUST
    /// read as unchanged; the alignment has to run on whichever side is the element.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UntypedCurrent_VsTypedIncomingMaterializingDefaults_IsUnchanged()
    {
        var current = Node(Element("""{"$type":"PluginCatalogContent","sourceRepoPath":"/repo"}"""));
        var incoming = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeTrue(
                "an untyped persisted element vs a typed incoming with only materialized defaults " +
                "is NOT a change — this exact asymmetry re-wrote FractalStars/Stars on every install");
    }

    /// <summary>An authored value differing from the persisted one is still a change — mirror side.</summary>
    [Fact(Timeout = 30000)]
    public void UntypedCurrent_TypedIncomingWithRealChange_IsStillDetected()
    {
        var current = Node(Element(
            """{"$type":"PluginCatalogContent","sourceRepoPath":"/repo","sourceRef":"v2"}"""));
        var incoming = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse("sourceRef v2 → HEAD is a real change the mirror alignment must not mask");
    }

    /// <summary>A $type change is never masked by coercing the persisted element into the incoming's type.</summary>
    [Fact(Timeout = 30000)]
    public void UntypedCurrentWithDifferentType_VsTypedIncoming_IsStillDetected()
    {
        var current = Node(Element("""{"$type":"SomeOtherContent","sourceRepoPath":"/repo"}"""));
        var incoming = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse("a differing $type IS a real change — alignment only applies same-type");
    }

    /// <summary>
    /// A member the persisted element carries but the incoming type lacks routes to the raw compare
    /// (change detected) — the strict Disallow guard, mirror side. The following rewrite normalizes
    /// the persisted shape; a silent drop would mask the schema drift forever.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UnknownPersistedProperty_VsTypedIncoming_IsStillDetected()
    {
        var current = Node(Element(
            """{"$type":"PluginCatalogContent","sourceRepoPath":"/repo","legacyProperty":"x"}"""));
        var incoming = Node(new PluginCatalogContent { SourceRepoPath = "/repo" });

        PackageInstaller.IsUnchanged(current, incoming, Mesh.JsonSerializerOptions)
            .Should().BeFalse(
                "a persisted member the incoming type lacks is schema drift — raw compare, one " +
                "normalizing rewrite, never a silent drop");
    }
}
