using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
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
