using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the structural sharing of type definitions between a hub's registry and its parent's (#7082,
/// <c>Doc/Architecture/PortalHeapIsHubs</c> → "Per-hub DI and type metadata"). A hub's configuration
/// registers most of the types the mesh registry already holds under the same name; measured on a node
/// hub with the standard data-source setup, 344 own definitions against 427 resolvable. A copy is
/// redundant exactly when it would be indistinguishable from the parent's definition, and then the hub
/// registry serves the parent's instead of holding one.
///
/// <para>The negative controls pin the boundary: a type whose key would come from the hub registry's
/// OWN key-function providers, a type registered under a different name, and a parent definition with an
/// explicit key all still get a definition of the hub's own.</para>
/// </summary>
public class HubRegistryInheritsEquivalentDefinitionsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int HubCount = 32;

    /// <summary>A type whose key the conventions decide (<c>Id</c>).</summary>
    [Display(Name = "Keyed content")]
    public record KeyedContent(string Id, string Label);

    /// <summary>A type with no conventional key: a registry's own providers would decide it.</summary>
    public record KeylessContent(string Label);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .WithType(typeof(KeyedContent), nameof(KeyedContent))
            .WithType(typeof(KeylessContent), nameof(KeylessContent));

    private IMessageHub[] HostedHubs(Func<MessageHubConfiguration, MessageHubConfiguration> configure) =>
        Enumerable.Range(0, HubCount)
            .Select(i => Mesh.GetHostedHub(new Address("inherits", i.ToString()),
                c => configure(c).WithPostingIdentity(PostingIdentity.System)))
            .ToArray();

    /// <summary>
    /// Fails on the code before the fix, which held one definition per hub: 32 hubs, 32 copies of a
    /// definition identical to the mesh's.
    /// </summary>
    [Fact]
    public void HubsRegisteringAConventionallyKeyedMeshType_HoldNoCopy()
    {
        var meshDefinition = Mesh.TypeRegistry.GetTypeDefinition(typeof(KeyedContent), create: false);
        meshDefinition.Should().NotBeNull();

        var definitions = HostedHubs(c => c.WithType(typeof(KeyedContent), nameof(KeyedContent)))
            .Select(h => h.TypeRegistry.GetTypeDefinition(typeof(KeyedContent), create: false))
            .ToArray();

        definitions.Distinct(ReferenceEqualityComparer.Instance).Should().ContainSingle(
            "the mesh's definition is the one each hub would build, so no hub holds a copy");
        definitions[0].Should().BeSameAs(meshDefinition);
        var content = new KeyedContent("k1", "label");
        definitions[0]!.GetKey(content).Should().Be("k1");
        definitions[0]!.CollectionName.Should().Be(nameof(KeyedContent));
        definitions[0]!.DisplayName.Should().Be("Keyed content");
    }

    /// <summary>Negative control: a type the conventions do not key keeps a definition per hub.</summary>
    [Fact]
    public void HubsRegisteringAKeylessType_KeepTheirOwnDefinitions()
    {
        var definitions = HostedHubs(c => c.WithType(typeof(KeylessContent), nameof(KeylessContent)))
            .Select(h => h.TypeRegistry.GetTypeDefinition(typeof(KeylessContent), create: false))
            .ToArray();

        definitions.Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(HubCount,
            "its key would come from each registry's own key-function providers");
    }

    /// <summary>Negative control: a different name is a registration of the hub's own.</summary>
    [Fact]
    public void ADifferentName_IsAnOwnRegistration()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(KeyedContent), "MeshName");
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(KeyedContent), "HubName");

        hub.TryGetCollectionName(typeof(KeyedContent), out var name).Should().BeTrue();
        name.Should().Be("HubName");
        hub.GetTypeDefinition(typeof(KeyedContent), create: false)
            .Should().NotBeSameAs(mesh.GetTypeDefinition(typeof(KeyedContent), create: false));
    }

    /// <summary>
    /// Negative control: a parent definition with an EXPLICIT key is not the one the hub would build, so
    /// the hub keeps its own, conventionally keyed definition — and an explicit key set on a hub for an
    /// inherited type is the hub's, found by type and by name, never written into the parent.
    /// </summary>
    [Fact]
    public void ExplicitKeys_StayWithTheRegistryThatSetThem()
    {
        var content = new KeyedContent("k1", "label");
        var mesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(KeyedContent), nameof(KeyedContent));
        mesh.WithKeyFunction(nameof(KeyedContent), new KeyFunction(_ => "mesh-key", typeof(string)));

        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(KeyedContent), nameof(KeyedContent));
        hub.GetTypeDefinition(typeof(KeyedContent), create: false)!.GetKey(content).Should().Be("k1",
            "the hub registered the type itself, and its own definition is keyed by the conventions");
        mesh.GetTypeDefinition(typeof(KeyedContent), create: false)!.GetKey(content).Should().Be("mesh-key");

        var plainMesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(KeyedContent), nameof(KeyedContent));
        var keyedHub = MessageHubExtensions.CreateTypeRegistry(plainMesh).WithType(typeof(KeyedContent), nameof(KeyedContent));
        keyedHub.GetTypeDefinition(typeof(KeyedContent), create: false)
            .Should().BeSameAs(plainMesh.GetTypeDefinition(typeof(KeyedContent), create: false), "the precondition: shared");
        keyedHub.WithKeyFunction(nameof(KeyedContent), new KeyFunction(_ => "hub-key", typeof(string)));
        keyedHub.GetTypeDefinition(typeof(KeyedContent), create: false)!.GetKey(content).Should().Be("hub-key");
        keyedHub.GetTypeDefinition(nameof(KeyedContent))!.GetKey(content).Should().Be("hub-key");
        keyedHub.GetKeyFunction(typeof(KeyedContent))!.Function(content).Should().Be("hub-key");
        plainMesh.GetTypeDefinition(typeof(KeyedContent), create: false)!.GetKey(content).Should().Be("k1",
            "the parent's definition is untouched");
    }
    /// <summary>A second conventionally keyed type, to replace a name's type on the parent.</summary>
    public record OtherKeyedContent(string Id, int Rank);

    /// <summary>A conventionally keyed closed generic.</summary>
    public record GenericKeyed<T>(string Id, T Value);

    /// <summary>
    /// A shared definition is bound LOCALLY: a later change to the parent — a key function, or a module
    /// replacing the name's type while the mesh runs — never changes what the hub registered.
    /// </summary>
    [Fact]
    public void AParentChangedAfterTheHubRegistered_DoesNotChangeTheHubsBinding()
    {
        var content = new KeyedContent("k1", "label");
        var mesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(KeyedContent), nameof(KeyedContent));
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(KeyedContent), nameof(KeyedContent));
        hub.GetTypeDefinition(typeof(KeyedContent), create: false)
            .Should().BeSameAs(mesh.GetTypeDefinition(typeof(KeyedContent), create: false), "the precondition: shared");

        mesh.WithKeyFunction(nameof(KeyedContent), new KeyFunction(_ => "mesh-key", typeof(string)));
        hub.GetTypeDefinition(typeof(KeyedContent), create: false)!.GetKey(content).Should().Be("k1",
            "the hub registered the conventionally keyed type; a later parent key is the parent's");
        hub.GetKeyFunction(nameof(KeyedContent))!.Function(content).Should().Be("k1");

        mesh.WithType(typeof(OtherKeyedContent), nameof(KeyedContent));
        hub.GetType(nameof(KeyedContent)).Should().Be(typeof(KeyedContent),
            "a replacement on the parent must not rebind the name the hub registered");
    }

    /// <summary>
    /// A closed generic registered under a custom name on both parent and hub keeps that name on the hub
    /// — the wire discriminator — rather than the synthesised generic name.
    /// </summary>
    [Fact]
    public void AClosedGenericUnderACustomName_KeepsItsNameOnTheHub()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(GenericKeyed<int>), "Entries");
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(GenericKeyed<int>), "Entries");

        hub.TryGetCollectionName(typeof(GenericKeyed<int>), out var name).Should().BeTrue();
        name.Should().Be("Entries");
    }

    /// <summary>
    /// A by-type match on the parent chain does not prove the NAME resolves to it: root binds A as
    /// <c>Entry</c>, a middle registry binds B as <c>Entry</c>, and a grandchild that registers A as
    /// <c>Entry</c> must resolve <c>Entry</c> to A.
    /// </summary>
    [Fact]
    public void AnIntermediateRebindingOfTheName_DoesNotShowThroughTheGrandchild()
    {
        var root = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(KeyedContent), "Entry");
        var middle = MessageHubExtensions.CreateTypeRegistry(root).WithType(typeof(OtherKeyedContent), "Entry");
        var grandchild = MessageHubExtensions.CreateTypeRegistry(middle).WithType(typeof(KeyedContent), "Entry");

        grandchild.GetType("Entry").Should().Be(typeof(KeyedContent),
            "the grandchild registered KeyedContent as Entry itself");
    }
}
