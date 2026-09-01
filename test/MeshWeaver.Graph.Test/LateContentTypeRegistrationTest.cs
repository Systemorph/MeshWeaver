using System;
using System.Reactive.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A content type that is not registered YET must not degrade a node FOREVER
/// (Systemorph/MeshWeaver#2952).</b>
///
/// <para>Every read seam types a node's content from what is registered at the instant the emission
/// passes through it. For an in-mesh NodeType — <c>Source/*.cs</c> compiled by Roslyn at RUNTIME —
/// "not registered" is a state that ENDS: the type becomes known when the compile calls
/// <c>MeshDataSource.WithContentType</c>, typically a few hundred milliseconds after the portal
/// started loading nodes. Nothing observed that registration, so a reader that opened on the losing
/// side of the race held an untyped <see cref="JsonElement"/> for the life of the hub — the node
/// never changes, so no further emission ever arrives to re-convert it. The view renders empty, the
/// content refuses edits, and every reactive wait for the typed shape times out. Nothing about it
/// is random; it is a race whose loser never recovers, which is exactly why re-running "fixes" it.
/// </para>
///
/// <para><b>What this test drives is the real thing</b>, not a helper: a node created with a
/// discriminator nothing can resolve, ONE live <c>GetMeshNodeStream</c> subscription held across the
/// registration, and the assertion that the SAME subscription is handed the content typed once the
/// type arrives. On <c>main</c> the second wait times out — the first (untyped) emission is the only
/// one there will ever be.</para>
///
/// <para>The content type is emitted into a COLLECTIBLE dynamic assembly, which is the CLR shape a
/// runtime-compiled NodeType produces (per-compile identity, and
/// <c>PolymorphicTypeInfoResolver</c> refuses to auto-adopt it into any hub's registry) — the same
/// modelling <c>ReimportTypedContentRecoveryTest</c> uses, without dragging Roslyn into the test.
/// </para>
/// </summary>
public class LateContentTypeRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Fresh mesh per test: the whole point is a registration that does NOT exist yet, so
    /// nothing a sibling test registered may leak in.</summary>
    protected override bool ShareMeshAcrossTests => false;

    /// <summary>
    /// 🚨 THE assertion: one subscription, opened while the type is unknown, must be handed the
    /// content TYPED when the type registers — without the node changing, without re-subscribing,
    /// and without anything polling for it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALiveReader_IsHandedTypedContent_WhenTheTypeRegistersAfterTheRead()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var contentType = EmitCollectibleType("LateBoundGadget", "Label");
        contentType.Assembly.IsCollectible.Should().BeTrue(
            "the emitted assembly must model a NodeType compiled at runtime (loaded collectible)");
        registry.TryResolveByDiscriminator(contentType.Name, out _).Should().BeFalse(
            "PRECONDITION: the mesh must not know this content type yet — that is the race's losing side");

        var partition = "lcr" + Guid.NewGuid().ToString("N")[..9];
        var typePath = $"{partition}/Gadget";
        var instancePath = $"{partition}/Live";

        // The NodeType DECLARATION, with no compile lifecycle attached: the instance below needs a
        // resolvable NodeType (the create pipeline refuses one that names nothing), and a
        // declaration with no Configuration is the cheapest way to have one without asking Roslyn
        // for a build inside a test about serialization.
        await meshService.CreateNode(new MeshNode("Gadget", partition)
        {
            Name = "Gadget",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Description = "A NodeType with no compile lifecycle" }
        }).Should().Within(TestTimeouts.Convergence).Emit("the NodeType declaration must land before its instance");

        // Content exactly as storage holds it for an instance of a runtime-compiled type: a JSON
        // object carrying the bare short-name $type. Serialised THROUGH the mesh options so the
        // discriminator and the property casing are what the platform actually writes.
        var instance = Activator.CreateInstance(contentType)!;
        contentType.GetProperty("Label")!.SetValue(instance, "live frame");
        var storedJson = JsonSerializer.Serialize(instance, contentType, Mesh.JsonSerializerOptions);
        Output.WriteLine($"stored content: {storedJson}");
        var stored = JsonSerializer.Deserialize<JsonElement>(storedJson);
        stored.TryGetProperty("$type", out var discriminator).Should().BeTrue(
            "PRECONDITION: the stored row must carry the discriminator the reader has to resolve");
        discriminator.GetString().Should().Be(contentType.Name);

        await meshService.CreateNode(new MeshNode("Live", partition)
        {
            Name = "Live frame",
            NodeType = typePath,
            State = MeshNodeState.Active,
            Content = stored
        }).Should().Within(TestTimeouts.Convergence).Emit("the instance carrying the unresolvable $type must land");

        // 🚨 ONE live subscription, held across the registration — the shape of a bound view, and
        // the only shape in which the defect is visible at all. Replay(1) + Connect keeps exactly
        // one upstream read open; a second GetMeshNodeStream call would open a fresh one and
        // re-convert from scratch, which is precisely the self-heal the real GUI never gets.
        var live = Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Where(n => n is not null)
            .Replay(1);
        using var connection = live.Connect();

        var degraded = await live.FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the node must be readable even while its type is unknown");
        degraded.Content.Should().BeOfType<JsonElement>(
            "BUG REPRODUCED: nothing can resolve the discriminator yet, so the read boundary hands "
            + "the subscriber an untyped JsonElement — every 'Content is T' downstream fails");

        // The compile finishing is exactly this call: MeshDataSource.WithContentType records the
        // compiled CLR type in the mesh-wide registry under the NodeType's path.
        registry.Register(contentType, typePath);

        var typed = await live.Where(n => n.Content is not JsonElement).FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the SAME subscription must be handed the content typed once the type registers — "
                + "the node never changes, so a reader that is not told about the registration waits "
                + "for an emission that will never come (#2952)");

        typed.Content!.GetType().Should().Be(contentType,
            "the re-type must land on the exact registered CLR type, not merely stop being a JsonElement");
        contentType.GetProperty("Label")!.GetValue(typed.Content).Should().Be("live frame",
            "the re-type must round-trip the payload, not just the type tag");
        typed.Path.Should().Be(instancePath);
    }

    /// <summary>
    /// The complement that keeps the wait honest: a registration for an UNRELATED type must not
    /// make a genuinely unresolvable node look typed. Without this, "re-emit on any registration"
    /// could pass the test above by simply re-emitting whatever it had.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnUnrelatedRegistration_LeavesGenuinelyUnresolvableContentUntyped()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var partition = "lcu" + Guid.NewGuid().ToString("N")[..9];
        var typePath = $"{partition}/Gadget";
        var instancePath = $"{partition}/Live";

        await meshService.CreateNode(new MeshNode("Gadget", partition)
        {
            Name = "Gadget",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Description = "A NodeType with no compile lifecycle" }
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var stored = JsonSerializer.Deserialize<JsonElement>(
            """{"$type":"AContentTypeTheMeshNeverCompiled","label":"orphan"}""");
        await meshService.CreateNode(new MeshNode("Live", partition)
        {
            Name = "Live frame",
            NodeType = typePath,
            State = MeshNodeState.Active,
            Content = stored
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var live = Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Where(n => n is not null)
            .Replay(1);
        using var connection = live.Connect();

        (await live.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit())
            .Content.Should().BeOfType<JsonElement>();

        // A registration that says nothing about this node's discriminator.
        registry.Register(EmitCollectibleType("SomeOtherGadget", "Label"), $"{partition}/Other");

        await live.Where(n => n.Content is not JsonElement).FirstAsync()
            .Should().NotEmit(5.Seconds(),
                "an unresolvable discriminator stays an untyped JsonElement — the wait re-asks the "
                + "registry and keeps the answer only when it is genuinely typed, so it can never "
                + "force-fit content onto an unrelated registration");
    }

    /// <summary>
    /// 🚨 The case that could falsify the wait's cheap pre-filter. To avoid re-deserializing the
    /// document for every unrelated registration, the seam first asks a string-only question — "could
    /// THIS registration resolve THIS node?" — and a filter narrowed to the NodeType path alone would
    /// look correct and silently re-open the whole defect for the NAME route.
    ///
    /// <para>So: the registration carries NO NodeType path (the <c>WithMeshType</c> / sweep-probe
    /// shape), and the node's own NodeType names something else entirely. Only the bare <c>$type</c>
    /// discriminator connects them — which is exactly what <c>TryRecover</c> resolves on, and exactly
    /// what a path-only filter would drop.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARegistrationWithNoNodeTypePath_StillRetypesByDiscriminator()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var contentType = EmitCollectibleType("NamelessRouteGadget", "Label");
        var partition = "lcn" + Guid.NewGuid().ToString("N")[..9];
        var typePath = $"{partition}/Gadget";
        var instancePath = $"{partition}/Live";

        await meshService.CreateNode(new MeshNode("Gadget", partition)
        {
            Name = "Gadget",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Description = "A NodeType with no compile lifecycle" }
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var instance = Activator.CreateInstance(contentType)!;
        contentType.GetProperty("Label")!.SetValue(instance, "by name only");
        var stored = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(instance, contentType, Mesh.JsonSerializerOptions));

        await meshService.CreateNode(new MeshNode("Live", partition)
        {
            Name = "Live frame",
            NodeType = typePath,
            State = MeshNodeState.Active,
            Content = stored
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var live = Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Where(n => n is not null)
            .Replay(1);
        using var connection = live.Connect();

        (await live.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit())
            .Content.Should().BeOfType<JsonElement>("nothing resolves the discriminator yet");

        // 🚨 No nodeTypePath — and the node's NodeType ($"{partition}/Gadget") is NOT it. The bare
        // discriminator is the only link.
        registry.Register(contentType);

        var typed = await live.Where(n => n.Content is not JsonElement).FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the name route must re-type too — a wait that only listens for its own NodeType path "
                + "would drop every WithMeshType / sweep-probe registration and leave the node untyped "
                + "forever, which is the defect wearing a different hat");

        typed.Content!.GetType().Should().Be(contentType);
        contentType.GetProperty("Label")!.GetValue(typed.Content).Should().Be("by name only");
    }

    /// <summary>
    /// Emits a minimal public class with one string property into a COLLECTIBLE dynamic assembly —
    /// the CLR shape of a compiled dynamic-node content type without dragging Roslyn into the test.
    /// (Mirrors <c>ReimportTypedContentRecoveryTest.EmitCollectibleType</c>.)
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
        var setter = typeBuilder.DefineMethod($"set_{propertyName}", accessorAttributes, null, [typeof(string)]);
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
