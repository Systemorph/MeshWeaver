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
/// Pins the sharing of immutable type descriptions (#5555, #1186): every hub owns its own
/// <see cref="ITypeRegistry"/> and its own <see cref="TypeDefinition"/>s — collection name, key
/// function and owning address are per-hub registration state — but what a CLR type DECLARES about
/// itself is described ONCE per mesh. A retained-hub heap census counted 3,662,876 definitions for
/// 378 CLR types with 3,652,612 separate description graphs: one per hub per type.
///
/// <para>The negative controls are what make the positive ones mean something: two INDEPENDENT
/// meshes must NOT share a description (a process-wide static table would pass the positive test
/// and fail this one — the sharing must be mesh-scoped), and a definition built outside any registry
/// owns a private one.</para>
/// </summary>
public class TypeDisplayMetadataSharedAcrossHubsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int HubCount = 32;

    /// <summary>A type with a display declaration and an icon, so its description is non-trivial.</summary>
    [Display(Name = "Described content", GroupName = "Sharing")]
    [Icon("test", "described")]
    public record DescribedContent(string Label);

    [Fact]
    public void HubsOfOneMesh_RegisterTheSameType_ShareOneDescription()
    {
        var definitions = Enumerable.Range(0, HubCount)
            .Select(i => Mesh.GetHostedHub(new Address("described", i.ToString()),
                c => c.WithType(typeof(DescribedContent), nameof(DescribedContent))
                    .WithPostingIdentity(PostingIdentity.System)))
            .Select(hub => hub.TypeRegistry.GetTypeDefinition(typeof(DescribedContent), create: false))
            .Select(d => d.Should().BeOfType<TypeDefinition>().Subject)
            .ToArray();

        // Per-hub registration semantics are unchanged: every hub owns its own definition …
        definitions.Distinct(ReferenceEqualityComparer.Instance).Should().HaveCount(HubCount,
            "each hub registered the type itself and owns that registration");
        // … and all of them read ONE description.
        definitions.Select(d => d.DisplayMetadata).Distinct(ReferenceEqualityComparer.Instance)
            .Should().ContainSingle("the description of a CLR type is immutable and mesh-scoped");
        definitions.Should().AllSatisfy(d =>
        {
            d.DisplayName.Should().Be("Described content");
            d.GroupName.Should().Be("Sharing");
            d.Icon.Should().Be(new Icon("test", "described"));
        });

        // The mesh hub's own registry — the root of the chain — hands out the same instance.
        var meshDefinition = Mesh.TypeRegistry.GetTypeDefinition(typeof(DescribedContent))
            .Should().BeOfType<TypeDefinition>().Subject;
        meshDefinition.DisplayMetadata.Should().BeSameAs(definitions[0].DisplayMetadata);
    }

    [Fact]
    public void IndependentMeshes_DoNotShareDescriptions()
    {
        // Negative control: the sharing is scoped to ONE mesh. A static table would make these equal.
        var meshA = MessageHubExtensions.CreateTypeRegistry();
        var meshB = MessageHubExtensions.CreateTypeRegistry();
        var a1 = Describe(MessageHubExtensions.CreateTypeRegistry(meshA).WithType(typeof(DescribedContent), "A1"));
        var a2 = Describe(MessageHubExtensions.CreateTypeRegistry(meshA).WithType(typeof(DescribedContent), "A2"));
        var b1 = Describe(MessageHubExtensions.CreateTypeRegistry(meshB).WithType(typeof(DescribedContent), "B1"));

        a1.Should().NotBeSameAs(a2, "each hub-level registry owns its own definition");
        a1.DisplayMetadata.Should().BeSameAs(a2.DisplayMetadata, "both registries chain to mesh A");
        b1.DisplayMetadata.Should().NotBeSameAs(a1.DisplayMetadata, "mesh B is a different mesh");

        // A definition constructed outside any registry owns a private description.
        new TypeDefinition(typeof(DescribedContent), "Standalone", new KeyFunctionBuilder())
            .DisplayMetadata.Should().NotBeSameAs(a1.DisplayMetadata);
    }

    [Fact]
    public void SharedDescription_KeepsKeyFunctionsAndNamesPerRegistry()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry();
        var first = MessageHubExtensions.CreateTypeRegistry(mesh)
            .WithKeyFunctionProvider(t => t == typeof(DescribedContent) ? new KeyFunction(_ => "first", typeof(string)) : null)
            .WithType(typeof(DescribedContent), "FirstName");
        var second = MessageHubExtensions.CreateTypeRegistry(mesh)
            .WithKeyFunctionProvider(t => t == typeof(DescribedContent) ? new KeyFunction(_ => "second", typeof(string)) : null)
            .WithType(typeof(DescribedContent), "SecondName");
        var firstDefinition = Describe(first);
        var secondDefinition = Describe(second);
        var content = new DescribedContent("x");

        firstDefinition.DisplayMetadata.Should().BeSameAs(secondDefinition.DisplayMetadata);
        firstDefinition.CollectionName.Should().Be("FirstName");
        secondDefinition.CollectionName.Should().Be("SecondName");
        firstDefinition.GetKey(content).Should().Be("first");
        secondDefinition.GetKey(content).Should().Be("second");
    }

    [Fact]
    public void BasicTypes_AreSeededOnlyAtTheRootOfTheChain()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry();
        var hubRegistries = Enumerable.Range(0, HubCount)
            .Select(_ => MessageHubExtensions.CreateTypeRegistry(mesh))
            .ToArray();
        var meshString = mesh.GetTypeDefinition(typeof(string), create: false);
        mesh.TryGetCollectionName(typeof(int?), out var meshNullableName).Should().BeTrue();

        meshString.Should().NotBeNull();
        hubRegistries.Should().AllSatisfy(r =>
        {
            r.GetTypeDefinition(typeof(string), create: false).Should().BeSameAs(meshString,
                "a hub-level registry resolves the basic types through the mesh registry");
            r.TryGetCollectionName(typeof(int?), out var name).Should().BeTrue();
            name.Should().Be(meshNullableName);
            r.GetType("Int32").Should().Be(typeof(int));
        });

        // Negative control: an independent root seeds its OWN basic types.
        MessageHubExtensions.CreateTypeRegistry().GetTypeDefinition(typeof(string), create: false)
            .Should().NotBeNull().And.NotBeSameAs(meshString);
    }

    private static TypeDefinition Describe(ITypeRegistry registry)
        => registry.GetTypeDefinition(typeof(DescribedContent), create: false)
            .Should().BeOfType<TypeDefinition>().Subject;
}
