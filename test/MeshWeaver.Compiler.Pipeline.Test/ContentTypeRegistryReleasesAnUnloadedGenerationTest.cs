using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The mesh-wide content-type registry must let go of a collectible generation when it unloads.
/// Measured: after a live swap of the real MeshWeaver.AI, a heap dump showed
/// <see cref="MeshContentTypeRegistry"/>'s discriminator map as the ONLY strong root of the swapped-out
/// generation — its content types were registered (by name and by NodeType path) and nothing
/// re-registered them. The registry now evicts, on the context's <c>Unloading</c>, exactly the entries
/// whose type belongs to it. The negative control: a type from a context that is NOT unloaded stays
/// resolvable.
/// </summary>
public sealed class ContentTypeRegistryReleasesAnUnloadedGenerationTest
{
    [Fact]
    public void AnUnloadedGenerationsContentTypes_AreEvicted_AndTheGenerationIsCollected()
    {
        var registry = new MeshContentTypeRegistry();
        var kept = Load("MeshWeaver.Test.KeptContent", "KeptContent", out var keptContext);
        registry.Register(kept, "type/Kept");

        var weak = RegisterAndUnload(registry);
        for (var round = 0; round < 10 && weak.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.IsAlive.Should().BeFalse("the registry must not pin the unloaded generation");
        registry.TryResolveByDiscriminator("EvictedContent", out _).Should().BeFalse();
        registry.TryResolveByNodeType("type/Evicted", out _).Should().BeFalse();
        registry.TryResolveByDiscriminator("KeptContent", out var stillThere).Should().BeTrue(
            "the control: a type whose context is NOT unloading keeps its claim");
        stillThere.Should().BeSameAs(kept);
        GC.KeepAlive(keptContext);
    }

    /// <summary>
    /// 🚨 Systemorph/MeshWeaver.Plugins#2799 — <c>Unload()</c> only INITIATES an unload. While
    /// something still holds the generation (the node-stream cache holds the typed content it last
    /// emitted; a hub may still be running on it) the type must keep answering: measured on an
    /// outgoing memex-cloud pod during a roll, fifteen reads of <c>Ops/Status/*</c> degraded to
    /// untyped content for a type that pod had typed for 45 minutes, because its hubs were torn
    /// down, the contexts began unloading, and the registry dropped the entries at that instant
    /// with no successor ever registering there.
    /// <para><b>Should fail if</b> the registry removes an unloading generation's entries outright
    /// (every assertion after the unload).</para>
    /// </summary>
    [Fact]
    public void AnUnloadingGenerationsContentTypes_KeepAnswering_WhileTheGenerationIsStillAlive()
    {
        var registry = new MeshContentTypeRegistry();
        var type = Load("MeshWeaver.Test.LiveContent", "LiveContent", out var context);
        registry.Register(type, "type/Live");
        // What the node-stream cache holds: a typed object of the generation.
        var held = Activator.CreateInstance(type, "held by a reader");

        context.Unload();
        for (var round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        registry.TryResolveByNodeType("type/Live", out var byPath).Should().BeTrue(
            "the generation is still loaded and in use — the old build keeps working until a newer one is bound");
        byPath.Should().BeSameAs(type);
        registry.TryResolveByDiscriminator("LiveContent", out var byName).Should().BeTrue();
        byName.Should().BeSameAs(type);

        using var document = System.Text.Json.JsonDocument.Parse("""{"$type":"LiveContent","text":"read after the hub left"}""");
        var recovered = registry.TryRecoverForNodeType(
            "type/Live", document.RootElement,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        recovered.Should().NotBeNull("a read after the teardown is typed, not degraded");
        recovered!.GetType().Should().BeSameAs(type);
        GC.KeepAlive(held);
    }

    /// <summary>
    /// The demoted generation never outranks its successor: the first registration for the same
    /// NodeType / discriminator takes both names back, whether it lands before or after the old
    /// context began unloading.
    /// </summary>
    [Fact]
    public void ASuccessorRegistration_DisplacesTheDemotedGeneration()
    {
        var registry = new MeshContentTypeRegistry();
        var old = Load("MeshWeaver.Test.Rebuilt.V1", "RebuiltContent", out var oldContext);
        registry.Register(old, "type/Rebuilt");
        var held = Activator.CreateInstance(old, "still in use");
        oldContext.Unload();
        registry.TryResolveByNodeType("type/Rebuilt", out var whileSuperseding).Should().BeTrue();
        whileSuperseding.Should().BeSameAs(old, "CONTROL — the old build answers until the new one is bound");

        var successor = Load("MeshWeaver.Test.Rebuilt.V2", "RebuiltContent", out var successorContext);
        registry.Register(successor, "type/Rebuilt");

        registry.TryResolveByNodeType("type/Rebuilt", out var byPath).Should().BeTrue();
        byPath.Should().BeSameAs(successor);
        registry.TryResolveByDiscriminator("RebuiltContent", out var byName).Should().BeTrue();
        byName.Should().BeSameAs(successor);
        GC.KeepAlive(held);
        GC.KeepAlive(successorContext);
    }

    /// <summary>
    /// 🚨 A context nobody unloads explicitly is unloaded by its FINALIZER
    /// (<c>AssemblyLoadContext.Finalize</c> → <c>InitiateUnload</c> → <c>Unloading</c>), so the
    /// registry's eviction runs on the finalizer thread, where an exception ends the process. A first
    /// draft of the weak shadow recorded the unloading context in a <c>ConditionalWeakTable</c>
    /// there and took a whole test host down with a <c>NullReferenceException</c> out of
    /// <c>GC.RunFinalizers</c>. <b>Should fail if</b> the eviction touches anything that cannot be
    /// used from a finalizer: this test host dies, which no other assertion can report.
    /// </summary>
    [Fact]
    public void AContextThatIsFinalizedRatherThanUnloaded_IsReleasedWithoutFaultingTheFinalizer()
    {
        var registry = new MeshContentTypeRegistry();
        var type = RegisterAndDrop(registry);
        // Waited on the TYPE through a plain weak reference: asking the registry instead would hand
        // out a strong reference on every round, and a generation that keeps being asked for is
        // meant to stay loaded.
        for (var round = 0; round < 20 && type.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        type.IsAlive.Should().BeFalse("nothing holds the generation, so the context's finalizer ran and it was collected");
        registry.TryResolveByNodeType("type/Dropped", out _).Should().BeFalse(
            "the demoted entry dies with the generation");
        registry.TryResolveByDiscriminator("DroppedContent", out _).Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndDrop(MeshContentTypeRegistry registry)
    {
        var type = Load("MeshWeaver.Test.DroppedContent", "DroppedContent", out _);
        registry.Register(type, "type/Dropped");
        return new WeakReference(type);
    }

    /// <summary>
    /// The same finalizer path with the REGISTRY unreachable too — a disposed mesh. Its members may
    /// already be finalized when the context's finalizer runs the eviction, which is exactly where
    /// the first draft's <c>ConditionalWeakTable</c> threw. <b>Should fail if</b> the eviction uses a
    /// finalizable member: the test host dies.
    /// </summary>
    [Fact]
    public void ARegistryAndContextDroppedTogether_AreCollected_WithoutFaultingTheFinalizer()
    {
        var type = RegisterWithARegistryNobodyKeeps();
        for (var round = 0; round < 20 && type.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        type.IsAlive.Should().BeFalse("neither the registry nor anything else holds the generation");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterWithARegistryNobodyKeeps()
    {
        var registry = new MeshContentTypeRegistry();
        var type = Load("MeshWeaver.Test.OrphanContent", "OrphanContent", out _);
        registry.Register(type, "type/Orphan");
        return new WeakReference(type);
    }

    /// <summary>
    /// 🚨 A type that registers AFTER its context began unloading must not get a strong entry: the
    /// <c>Unloading</c> event fires once, so nothing would ever evict it and the registry would be
    /// the permanent root of a generation on its way out. It goes to the weak shadow — resolvable
    /// while held, released with the generation.
    /// <para><b>Should fail if</b> a late registration lands in the strong maps: the generation is
    /// never collected.</para>
    /// </summary>
    [Fact]
    public void ATypeRegisteredAfterItsContextBeganUnloading_IsResolvableWhileHeld_AndNeverRooted()
    {
        var registry = new MeshContentTypeRegistry();
        var late = RegisterAfterTheUnloadBegan(registry);
        for (var round = 0; round < 20 && late.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        late.IsAlive.Should().BeFalse(
            "a strong entry for a type whose context already fired Unloading is never evicted and pins the generation");
        registry.TryResolveByNodeType("type/Late", out _).Should().BeFalse();
        registry.TryResolveByDiscriminator("LateContentLate", out _).Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAfterTheUnloadBegan(MeshContentTypeRegistry registry)
    {
        var first = Load("MeshWeaver.Test.LateContent", "LateContent", out var context);
        registry.Register(first, "type/First");
        context.Unload();

        var late = first.Assembly.GetType("LateContentLate")!;
        registry.Register(late, "type/Late");
        registry.TryResolveByNodeType("type/Late", out var whileHeld).Should().BeTrue(
            "CONTROL — the late type still answers while its generation is loaded");
        whileHeld.Should().BeSameAs(late);
        return new WeakReference(late);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndUnload(MeshContentTypeRegistry registry)
    {
        var type = Load("MeshWeaver.Test.EvictedContent", "EvictedContent", out var context);
        registry.Register(type, "type/Evicted");
        registry.TryResolveByDiscriminator("EvictedContent", out _).Should().BeTrue();
        context.Unload();
        return new WeakReference(context);
    }

    private static Type Load(string assemblyName, string typeName, out AssemblyLoadContext context)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            // Two types per generation: the second one is what a LATE registration registers.
            [CSharpSyntaxTree.ParseText(
                $"public sealed record {typeName}(string Text); public sealed record {typeName}Late(string Text);")],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        compilation.Emit(buffer).Success.Should().BeTrue();
        buffer.Position = 0;
        context = new AssemblyLoadContext(assemblyName, isCollectible: true);
        return context.LoadFromStream(buffer).GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is not in the emitted {assemblyName}");
    }
}
