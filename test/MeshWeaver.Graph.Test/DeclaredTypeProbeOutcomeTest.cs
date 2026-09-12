using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A PROBE THAT DID NOT COMPLETE IS NOT AN ANSWER</b> — the decision
/// <see cref="CreatableTypesProvider"/> takes for a type path a config source NAMES
/// (<see cref="NodeTypeDefinition.CreatableTypes"/> or <c>MeshConfiguration.GlobalCreatableTypes</c>)
/// once the per-path lookup has run.
///
/// <para>There are THREE outcomes and only two of them are answers. A key PRESENT in the lookup
/// means the probe completed — its value is the node, or <c>null</c> for a confirmed absence. A key
/// ABSENT means the probe timed out or faulted, which is UNKNOWN. Folding unknown into "no such
/// node" is what would make the create opt-out hold while storage is healthy and lapse exactly when
/// it is not: a runtime NodeType carrying <c>ExcludeFromContext: ["create"]</c> would be
/// synthesised straight back into the menu by the transient failure of the very read added to
/// withhold it.</para>
///
/// <para>Tested directly rather than through the mesh because inducing a timeout on one path's
/// query would mean standing in a query-core double for the whole mesh — a heavier and less honest
/// test of a decision that is a pure function of the lookup.</para>
/// </summary>
public class DeclaredTypeProbeOutcomeTest
{
    private const string DeclaredPath = "Acme/Question";

    private static readonly MeshConfiguration Configuration = new(meshNodes: []);
    private static readonly IServiceProvider NoStaticNodes = new ServiceCollection().BuildServiceProvider();
    private static readonly JsonSerializerOptions Options = new();

    private static ImmutableDictionary<string, MeshNode?> Lookup() =>
        ImmutableDictionary<string, MeshNode?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private static CreatableTypeInfo? Decide(ImmutableDictionary<string, MeshNode?> lookup) =>
        CreatableTypesProvider.BuildInfoFromConfig(
            DeclaredPath, lookup, Configuration, NoStaticNodes, Options);

    /// <summary>
    /// Confirmed ABSENT — the probe ran and found nothing. The declaration is honoured: a type may
    /// be declared before the import that lands it, and synthesising the entry is deliberate.
    /// </summary>
    [Fact]
    public void AConfirmedAbsenceStillOffersTheDeclaredType()
    {
        var info = Decide(Lookup().SetItem(DeclaredPath, null));

        info.Should().NotBeNull(
            "the probe COMPLETED and found no node, so the declaration names a type the mesh does "
            + "not have yet — offering it is what lets a declaration precede its import");
        info!.NodeTypePath.Should().Be(DeclaredPath);
    }

    /// <summary>The probe found the node and it does not opt out — offered, with its own metadata.</summary>
    [Fact]
    public void AResolvedTypeThatDidNotOptOutIsOffered()
    {
        var node = new MeshNode("Question", "Acme")
        {
            Name = "Question", NodeType = MeshNode.NodeTypePath,
        };

        var info = Decide(Lookup().SetItem(DeclaredPath, node));

        info.Should().NotBeNull();
        info!.DisplayName.Should().Be("Question", "the resolved node is what the menu shows");
    }

    /// <summary>
    /// The probe found the node and it DOES opt out. A list that names it cannot resurrect it —
    /// this is the case the per-path lookup was added for.
    /// </summary>
    [Fact]
    public void AResolvedTypeThatOptedOutIsWithheld()
    {
        var node = new MeshNode("Question", "Acme")
        {
            Name = "Question",
            NodeType = MeshNode.NodeTypePath,
            ExcludeFromContext = ImmutableHashSet.Create(MeshContexts.Create),
        };

        Decide(Lookup().SetItem(DeclaredPath, node)).Should().BeNull(
            "ExcludeFromContext: [\"create\"] is the type's own statement that its instances are "
            + "made by the platform; a whitelist ADDS types the queries could not reach, it does "
            + "not overrule that");
    }

    /// <summary>
    /// 🚨 THE ONE THAT WOULD HAVE SHIPPED. The probe did NOT complete — the path is absent from the
    /// lookup. That is unknown, not absence, and it fails CLOSED.
    /// </summary>
    [Fact]
    public void AnUnavailableProbeWithholdsTheType_NeverSynthesisesIt()
    {
        Decide(Lookup()).Should().BeNull(
            "a timed-out or faulted probe carries no information about the type's opt-out. "
            + "Synthesising the entry would offer a type whose ExcludeFromContext simply could not "
            + "be read — the opt-out holding while storage is healthy and lapsing under load, "
            + "which is the one condition it most needs to hold under");
    }

    /// <summary>
    /// The control that keeps the assertion above from passing for the wrong reason: the SAME empty
    /// lookup returns an entry once the path is recorded as probed. If this failed, the test above
    /// would be observing "BuildInfoFromConfig never returns anything" rather than a fail-closed.
    /// </summary>
    [Fact]
    public void TheDifferenceIsTheRECORDEDPROBE_NotTheEmptyLookup()
    {
        Decide(Lookup()).Should().BeNull();
        Decide(Lookup().SetItem(DeclaredPath, null)).Should().NotBeNull(
            "one key, present with a null value, is the whole difference between unknown and "
            + "confirmed absent");
    }
}
