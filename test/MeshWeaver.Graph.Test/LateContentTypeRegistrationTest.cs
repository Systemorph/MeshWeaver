using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// 🚨 <b>The degradation this test produces is its SUBJECT, so it is captured here and asserted
    /// here — it must never reach the shared trace file.</b>
    ///
    /// <para><b>Why this exists (MeshWeaver#3625).</b> <c>check-untyped-content.sh</c> reds a shard
    /// whose <c>collected-logs/</c> carries a <c>MeshNodeContentDegradedException</c>. That gate is
    /// correct and it has no allow-list on purpose: an exemption in the gate would be a permanently
    /// green check wearing a reason. Its prescribed remedy — <i>seed a value of a DIFFERENT,
    /// REGISTERED type</i> — repairs a fixture that only needs "present but unreadable as
    /// <c>T</c>", and it repaired <c>UnreadablePolicyRecordIsNotClobberedTest</c>. It cannot apply
    /// here: this test's assertion IS that <b>nothing can resolve the content</b>, so seeding a
    /// resolvable type deletes what #2952 is about.</para>
    ///
    /// <para><b>What is done instead is the pattern this PR already set one test over.</b>
    /// <c>UntypedContentDegradationReachesTheTraceSinkTest</c> drives the production emitter against
    /// its OWN recording logger, so a real degradation proves reachability without writing into the
    /// shared file. The same move works for a whole mesh, because the emitter takes its logger from
    /// DI: this replaces <c>ILogger&lt;MeshNodeStreamCache&gt;</c> for THIS test class's mesh only.
    /// </para>
    ///
    /// <para>🚨 <b>Why this is not a skip-trapdoor.</b> Three properties, and all three are needed:
    /// <list type="number">
    /// <item>It captures <b>every</b> degradation record this mesh produces and forwards everything
    /// else untouched — no level is dialled, no category is silenced, nothing is dropped.</item>
    /// <item>Each test <b>asserts</b> that the record it declared as its subject was produced, that
    /// it names the expected node, and that it <b>would have satisfied the trace sink's own
    /// predicate</b>. So the diversion cannot be silently vacuous: if the platform stopped
    /// reporting the degradation, the test reds — which is strictly more than this test asserted
    /// before, when the record was written to a file nobody read.</item>
    /// <item>The same assertion pins that <b>everything captured is the declared node</b>. An
    /// unintended degradation of any other node in this mesh fails the test rather than being
    /// swallowed, so the exemption is exactly one node wide and cannot grow by accident.</item>
    /// </list>
    /// <c>UntypedContentDegradationGate.ADivertedDegradationIsAssertedWhereItIsDiverted</c> is the
    /// control arm: it fails the build if any test substitutes this logger without asserting what
    /// it caught.</para>
    /// </summary>
    private readonly DegradedContentRecorder degradations = new();

    /// <summary>Routes this mesh's degradation records into <see cref="degradations"/>. Registered
    /// as the CLOSED <c>ILogger&lt;MeshNodeStreamCache&gt;</c>, which wins over the open-generic
    /// <c>ILogger&lt;&gt;</c>, so only this one category is intercepted and only for this mesh.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILogger<MeshNodeStreamCache>>(
                sp => degradations.Forwarding(
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<MeshNodeStreamCache>())));

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

        // 🚨 The degradation is this test's SUBJECT, so it is asserted here rather than left to
        // land in the shared trace file, where check-untyped-content.sh would read it as a defect.
        degradations.AssertReportedFor(instancePath,
            "#2952 is the race whose LOSING side this is: the read boundary can only type content "
            + "from what is registered at the instant the emission passes through it, and the "
            + "registration lands later in this very test");

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

        degradations.AssertReportedFor(instancePath,
            "this test's assertion IS that a discriminator no declaration will ever claim stays "
            + "untyped — the degradation is permanent BY DESIGN here, which is exactly the case "
            + "the gate's 'seed a registered foreign type' remedy cannot model");

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

        degradations.AssertReportedFor(instancePath,
            "the NAME route's losing side is the same race as #2952's — the registration this test "
            + "makes carries no NodeType path, so only the bare discriminator connects them");

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
    /// 🚨 The private sink for this test class's degradation records — see
    /// <see cref="degradations"/> for why the records must not reach the shared trace file, and why
    /// capturing them without asserting them would be the exemption this whole gate refuses.
    ///
    /// <para>It is a DECORATOR, not a filter: only a record carrying
    /// <see cref="MeshNodeContentDegradedException"/> is taken; every other record the cache logs
    /// goes to the real logger unchanged, at its own level. Nothing about the mesh's logging is
    /// dialled down.</para>
    ///
    /// <para>Instance state guarded by a plain synchronous gate held around a pure field update:
    /// the records arrive on hub action-block threads, never on the test's, so a plain list would
    /// tear. No static anywhere — the recorder's lifetime is this test instance's mesh.</para>
    /// </summary>
    private sealed class DegradedContentRecorder : ILogger<MeshNodeStreamCache>
    {
        private readonly object gate = new();
        private ImmutableList<Captured> captured = ImmutableList<Captured>.Empty;
        private ILogger<MeshNodeStreamCache>? forwardTo;

        /// <summary>One captured degradation, with the two facts the trace sink's predicate reads.</summary>
        internal sealed record Captured(LogLevel Level, string Message, MeshNodeContentDegradedException Exception);

        /// <summary>Wires the real logger everything else is forwarded to, and returns this recorder
        /// so it can be registered inline as the closed <c>ILogger&lt;MeshNodeStreamCache&gt;</c>.</summary>
        internal DegradedContentRecorder Forwarding(ILogger<MeshNodeStreamCache> inner)
        {
            forwardTo = inner;
            return this;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => forwardTo?.BeginScope(state);

        bool ILogger.IsEnabled(LogLevel logLevel)
            // A degradation must be captured whatever the configured level says, or this recorder
            // would inherit the very filter the diversion exists to be independent of.
            => logLevel >= LogLevel.Warning || forwardTo?.IsEnabled(logLevel) == true;

        void ILogger.Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is MeshNodeContentDegradedException degraded)
            {
                lock (gate)
                    captured = captured.Add(new Captured(logLevel, formatter(state, exception), degraded));
                return;
            }

            forwardTo?.Log(logLevel, eventId, state, exception, formatter);
        }

        /// <summary>
        /// 🚨 The assertion that makes the diversion honest. Fails when the platform did NOT report
        /// the degradation this test reproduces, and fails when it reported one for any node other
        /// than <paramref name="nodePath"/>.
        /// </summary>
        /// <param name="nodePath">The node whose degradation is this test's declared subject.</param>
        /// <param name="because">Why that degradation is the subject rather than a defect.</param>
        internal void AssertReportedFor(string nodePath, string because)
        {
            var records = Snapshot();

            records.Should().NotBeEmpty(
                "the platform must REPORT the degradation this test reproduces — {0}. A test that "
                + "diverts degradation records away from the shared trace file and then finds none "
                + "has asserted nothing, which is the permanently-green exemption "
                + "check-untyped-content.sh refuses to carry (#3625)", because);

            records.Select(r => r.Exception.NodePath).Distinct().Should().Equal([nodePath],
                "the diversion is exactly ONE node wide: a degradation of any other node in this "
                + "mesh is not this test's subject and must red the shard through the gate, not be "
                + "swallowed here");

            var record = records[0];

            // Typed as the nullable `Exception?` the sink itself handles, deliberately: this line
            // restates XUnitFileLogger's own condition, and restating it over the shape the sink
            // actually sees is what makes it an EVALUATION of that condition rather than a
            // paraphrase of it.
            Exception? asTheSinkSeesIt = record.Exception;
            (asTheSinkSeesIt is not null && record.Level >= LogLevel.Warning).Should().BeTrue(
                "the captured record must be one the trace sink WOULD have written "
                + "(exception is not null && level >= Warning — XUnitFileLogger.Log → "
                + "TestTraceLog.AppendFault). If it would not have been written, this test proves "
                + "nothing about the signal it is keeping out of that file. Captured: level={0}",
                record.Level);
            record.Message.Should().Contain("stayed an untyped JsonElement",
                "the prose net check-untyped-content.sh keeps as its second key must still be in "
                + "the formatted message");
            record.Exception.NodeType.Should().NotBeNullOrEmpty(
                "the record must name the NodeType — that is the key a reader uses to decide "
                + "between 'the compile has not registered it yet' and 'nothing ever will'");
        }

        private ImmutableList<Captured> Snapshot()
        {
            lock (gate)
                return captured;
        }
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
