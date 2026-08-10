using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Root-cause pin for "a GitSync re-import of a partition with custom NodeTypes renders empty
/// until a manual recycle." A dynamically-compiled NodeType's <c>$type</c> → CLR-Type mapping was
/// registered ONLY as a per-hub side effect of <c>MeshDataSource.WithContentType</c>, into that
/// hub's frozen <see cref="JsonSerializerOptions"/>. Any hub whose options don't carry it — notably
/// the domain-agnostic mesh-node cache hub — deserialises that content to a bare
/// <see cref="JsonElement"/>, so every <c>Content is T</c> / <c>as T</c> downstream silently fails.
///
/// <para>These tests pin the cure — the mesh-wide <see cref="IMeshContentTypeRegistry"/> — at the
/// real degrade seam (<c>MeshNodeStreamExtensions.EnsureTypedContent</c>, internal, visible here):
/// WITHOUT the registry the content stays an untyped JsonElement (the bug is reproduced); WITH it,
/// the concrete type is recovered on the already-degraded path (the fix). A dynamic-node content
/// type is modelled by a COLLECTIBLE Reflection.Emit type — same CLR shape the compiler produces
/// (per-compile identity, <c>PolymorphicTypeInfoResolver</c> refuses to auto-adopt it) — without
/// dragging Roslyn into a unit test.</para>
/// </summary>
public class ReimportTypedContentRecoveryTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// The seam-level regression, at the recovery boundary the degrade seams
    /// (<c>MeshNodeStreamCache</c>, <c>EnsureTypedContent</c>) all funnel through. First it REPRODUCES
    /// the bug — a real hub's polymorphic options deserialise a dynamic-node <c>$type</c> they don't
    /// know to a bare <see cref="JsonElement"/> — then it pins the fix: the mesh-wide registry re-types
    /// that same element. Two states of the same input, exactly what fails without / passes with the fix.
    /// </summary>
    [Fact]
    public void DegradedContent_StaysUntypedWithoutRegistry_RecoversThroughRegistry()
    {
        var client = GetClient();
        var options = client.JsonSerializerOptions;

        var contentType = EmitCollectibleType("ReimportSlideContent", "Title");
        contentType.Assembly.IsCollectible.Should().BeTrue(
            "the emitted assembly must model a dynamic NodeType compilation (loaded collectible)");

        // A stored content row carrying the short-name $type — the shape a re-import re-materialises.
        // Serialise the instance THROUGH the hub options (as the owning hub would), so the $type
        // discriminator and property casing match exactly what the mesh persists.
        var instance = Activator.CreateInstance(contentType)!;
        contentType.GetProperty("Title")!.SetValue(instance, "Slide One");
        var json = JsonSerializer.Serialize(instance, contentType, options);
        json.Should().Contain(contentType.Name, "the owning hub stamps the short-name $type discriminator");
        var stored = JsonSerializer.Deserialize<JsonElement>(json);

        // BUG REPRODUCED: a real hub's options can't resolve the collectible $type, so the polymorphic
        // reader degrades it to a bare JsonElement — the untyped value that makes `content as T` null
        // and renders the page empty. (This assertion holds WITH or WITHOUT the fix — it documents the
        // defect the fix recovers from.)
        var degraded = JsonSerializer.Deserialize<object>(stored.GetRawText(), options);
        degraded.Should().BeOfType<JsonElement>(
            "a real hub's frozen options can't resolve a dynamic-node $type — it degrades to JsonElement");

        // FIX: the mesh-wide content-type registry (populated exactly as WithContentType populates it)
        // recovers the concrete CLR type on that already-degraded element — no per-hub registration,
        // no recycle.
        var registry = new MeshContentTypeRegistry();
        registry.Register(contentType);
        var recovered = registry.TryRecover((JsonElement)degraded!, options);

        recovered.Should().NotBeNull(
            "the mesh-wide content-type registry must re-type content that degraded to JsonElement");
        recovered!.GetType().Should().Be(contentType, "recovery must land on the exact registered CLR type");
        contentType.GetProperty("Title")!.GetValue(recovered).Should().Be("Slide One",
            "recovery must round-trip the payload, not just the type tag");
    }

    /// <summary>
    /// The resolver only heals what it can resolve: a discriminator NOT in the registry returns null
    /// (so the caller's genuine-corruption warning still fires) rather than being force-fit.
    /// </summary>
    [Fact]
    public void UnregisteredDiscriminator_IsNotRecovered()
    {
        var options = GetClient().JsonSerializerOptions;

        var registry = new MeshContentTypeRegistry();
        registry.Register(EmitCollectibleType("KnownContent", "Value"));

        var stored = JsonSerializer.Deserialize<JsonElement>(
            """{"$type":"SomeOtherContentTheMeshNeverCompiled","Value":"x"}""");

        registry.TryRecover(stored, options).Should().BeNull(
            "an unresolvable discriminator must NOT be recovered — the caller keeps its corruption warning");
    }

    /// <summary>The registry indexes a type by both its short and full name (both valid $type
    /// discriminators) and resolves the concrete type back.</summary>
    [Fact]
    public void MeshContentTypeRegistry_ResolvesByShortAndFullName()
    {
        var contentType = EmitCollectibleType("FactRecord", "Amount");
        var registry = new MeshContentTypeRegistry();
        registry.Register(contentType, nodeTypePath: "space/Fact");

        registry.TryResolveByDiscriminator(contentType.Name, out var byShort).Should().BeTrue();
        byShort.Should().Be(contentType);
        registry.TryResolveByDiscriminator(contentType.FullName!, out var byFull).Should().BeTrue();
        byFull.Should().Be(contentType);
        registry.TryResolveByNodeType("space/Fact", out var byNodeType).Should().BeTrue();
        byNodeType.Should().Be(contentType);

        registry.TryResolveByDiscriminator("Nope", out _).Should().BeFalse();
        registry.TryResolveByNodeType("space/Absent", out _).Should().BeFalse();
    }

    /// <summary>
    /// The SEAM wiring, end to end: drives the real degrade seam
    /// <c>MeshNodeStreamHandle.EnsureTypedContent</c> (the boundary every cross-hub read passes
    /// through). WITHOUT the mesh-wide registry the seam hands consumers a bare
    /// <see cref="JsonElement"/> (the bug); WITH it the seam re-types the content. This pins the
    /// wiring itself — deleting the registry call from the seam turns this test red — where
    /// <see cref="DegradedContent_StaysUntypedWithoutRegistry_RecoversThroughRegistry"/> pins only
    /// the recovery primitive.
    /// </summary>
    [Fact]
    public void EnsureTypedContent_Seam_RecoversViaRegistry_DegradesWithout()
    {
        var options = GetClient().JsonSerializerOptions;

        var contentType = EmitCollectibleType("SeamSlideContent", "Heading");
        var instance = Activator.CreateInstance(contentType)!;
        contentType.GetProperty("Heading")!.SetValue(instance, "Seam One");
        var stored = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(instance, contentType, options));
        var node = new MeshNode("n") { Content = stored };

        // Seam WITHOUT the registry → content stays a degraded JsonElement (the defect at the seam).
        MeshNodeStreamHandle.EnsureTypedContent(node, options, contentTypeRegistry: null)
            .Content.Should().BeOfType<JsonElement>(
                "without the registry the degrade seam hands consumers an untyped JsonElement");

        // Seam WITH the registry → EnsureTypedContent re-types via the registry (the wired fix).
        var registry = new MeshContentTypeRegistry();
        registry.Register(contentType);
        var typed = MeshNodeStreamHandle.EnsureTypedContent(node, options, registry);

        typed.Content!.GetType().Should().Be(contentType,
            "the seam must consult the mesh-wide registry on the degraded path, not just warn");
        contentType.GetProperty("Heading")!.GetValue(typed.Content).Should().Be("Seam One",
            "the seam must round-trip the payload, not merely the type tag");
    }

    /// <summary>
    /// 🚨 The WIRE seam — the FIRST place a dynamic NodeType's content degrades, and until now the only
    /// one that never consulted the mesh-wide registry.
    ///
    /// <para>Prod (memex.meshweaver.cloud, 2026-08-09) logged this continuously, for EVERY plugin's
    /// content type at once — <c>PluginContent</c> (the type of every Store/Plugin ROOT node, so
    /// <c>/Underwriting</c> itself), <c>GuidelineReference</c>, <c>RegionalLimitContent</c>,
    /// <c>CapacityCapContent</c>, <c>AppetiteMetricContent</c>, <c>Workbench</c>, <c>ChessGame</c>, …:
    /// <c>"Received '$type':'PluginContent' which is NOT registered in this (receiving) hub"</c>. The
    /// advice in that warning — register it on the receiving hub — is IMPOSSIBLE for a type that only
    /// exists after a runtime Roslyn compile, and <c>PolymorphicTypeInfoResolver</c> deliberately
    /// refuses to auto-adopt collectible types into a per-hub registry. So the node crossed the wire
    /// untyped, every <c>Content is X</c> soft-cast failed, and the page rendered empty.</para>
    ///
    /// <para>Two hubs, ONE payload, the only difference being whether the process-wide
    /// <see cref="IMeshContentTypeRegistry"/> is reachable from the receiving hub's DI: without it the
    /// bug reproduces, with it the converter re-types. Delete the registry lookup from
    /// <c>ObjectPolymorphicConverter.ReadObject</c> and this turns red.</para>
    /// </summary>
    [Fact]
    public void WireSeam_ForeignHubReadsDynamicContentTyped_OnlyThroughRegistry()
    {
        var contentType = EmitCollectibleType("WirePluginContent", "Body");
        var registry = new MeshContentTypeRegistry();
        registry.Register(contentType);

        // A hub that does NOT own the type and has NO mesh-wide registry — the shape every
        // pre-fix receiving hub had (cache hub, routing hub, a sibling per-node hub).
        var unaidedHub = GetClient();
        var instance = Activator.CreateInstance(contentType)!;
        contentType.GetProperty("Body")!.SetValue(instance, "hero markup");
        var stored = JsonSerializer.Serialize(instance, contentType, unaidedHub.JsonSerializerOptions);
        stored.Should().Contain(contentType.Name, "the owning hub stamps the short-name $type discriminator");

        // BUG REPRODUCED at the wire seam: the receiving hub's polymorphic converter cannot resolve the
        // discriminator, so Content arrives as a bare JsonElement.
        JsonSerializer.Deserialize<object>(stored, unaidedHub.JsonSerializerOptions)
            .Should().BeOfType<JsonElement>(
                "a receiving hub with no registration and no mesh-wide registry degrades dynamic content to JsonElement");

        // FIX: the SAME payload, read by a hub whose DI exposes the mesh-wide registry — the converter
        // recovers the concrete CLR type. No per-hub WithType, no recycle, no adoption into the hub's
        // own TypeRegistry (so the collectible assembly is not pinned by this hub).
        var aidedHub = GetClient(c => c
            .WithServices(s => s.AddSingleton<IMeshContentTypeRegistry>(registry))
            .WithPostingIdentity(PostingIdentity.System));

        var recovered = JsonSerializer.Deserialize<object>(stored, aidedHub.JsonSerializerOptions);

        recovered!.GetType().Should().Be(contentType,
            "the wire seam must consult the mesh-wide registry before degrading to JsonElement");
        contentType.GetProperty("Body")!.GetValue(recovered).Should().Be("hero markup",
            "recovery must round-trip the payload, not merely the type tag");

        // The receiving hub must NOT have adopted the collectible type: adopting a per-compile CLR
        // identity into a long-lived registry is what PolymorphicTypeInfoResolver refuses to do, and
        // the fix must not smuggle it in through the back door.
        aidedHub.TypeRegistry.TryGetType(contentType.Name, out _).Should().BeFalse(
            "recovery deserialises to the concrete type; it must not register the collectible type on the hub");
    }

    /// <summary>
    /// Emits a minimal public class with one string property into a COLLECTIBLE dynamic assembly —
    /// the CLR shape of a compiled dynamic-node content type without dragging Roslyn into the test.
    /// (Mirrors <c>TypeRegistryTest.EmitCollectibleType</c>.)
    /// </summary>
    private static Type EmitCollectibleType(string typeName, string propertyName)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"DynamicNode_{typeName}"), AssemblyBuilderAccess.RunAndCollect);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("main");
        var typeBuilder = moduleBuilder.DefineType(
            typeName, TypeAttributes.Public | TypeAttributes.Class);
        var field = typeBuilder.DefineField($"_{propertyName}", typeof(string), FieldAttributes.Private);
        var property = typeBuilder.DefineProperty(propertyName, PropertyAttributes.None, typeof(string), null);
        const MethodAttributes accessorAttributes =
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = typeBuilder.DefineMethod($"get_{propertyName}", accessorAttributes, typeof(string), Type.EmptyTypes);
        var getterIl = getter.GetILGenerator();
        getterIl.Emit(OpCodes.Ldarg_0);
        getterIl.Emit(OpCodes.Ldfld, field);
        getterIl.Emit(OpCodes.Ret);
        var setter = typeBuilder.DefineMethod($"set_{propertyName}", accessorAttributes, null, new[] { typeof(string) });
        var setterIl = setter.GetILGenerator();
        setterIl.Emit(OpCodes.Ldarg_0);
        setterIl.Emit(OpCodes.Ldarg_1);
        setterIl.Emit(OpCodes.Stfld, field);
        setterIl.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        return typeBuilder.CreateType()!;
    }
}
