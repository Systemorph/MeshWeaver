using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Live module update, slice 1: every module runs in its OWN collectible load context</b>
/// (policy <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para>Until this change every landed module was <c>Assembly.LoadFrom</c>-ed into the DEFAULT
/// context, which can neither unload an assembly nor hold two copies of one name — so no module
/// could ever change inside a running process, and every update was a restart. These pin the
/// foundation a live swap stands on: a module loads into a collectible context that still binds the
/// ONE platform; two generations of one module coexist; a retired generation is REALLY collected
/// (and a retained one is caught by name — the negative control); a dependent binds its dependency's
/// current generation; and a NodeType compiled against the newer generation's member binds it after
/// the swap — the <c>ThreadPreparation.Group</c> / <c>MissingMethodException</c> shape of the
/// 2026-10-05 incident, at the load-context level.</para>
///
/// <para>Real Roslyn emits, real load contexts, real collections — nothing mocked.</para>
/// </summary>
public sealed class ModulesRunInTheirOwnContextTest : IDisposable
{
    private const string Lib = "MeshWeaver.Test.LiveLib";
    private const string Dependent = "MeshWeaver.Test.LiveDependent";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-modules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a mapped DLL on a context still collecting — the OS reclaims it */ }
        catch (UnauthorizedAccessException) { }
    }

    // ───────────────────────────────────────────── the context, and the one platform

    [Fact]
    public void AnInstalledModule_RunsInItsOwnCollectibleContext_AndBindsTheOnePlatform()
    {
        var path = Write(Lib, "g1", LibSource(version: 1));
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "live"));

        builder.InstallAssemblies(path);

        var generation = builder.ModuleContexts.Current(Lib);
        generation.Should().NotBeNull("a module the image does not bind must be held by the registry");
        var context = AssemblyLoadContext.GetLoadContext(generation!.Assembly);
        context.Should().BeOfType<ModuleLoadContext>(
            "loaded into the default context the module can never be unloaded or swapped — the "
            + "pre-change behaviour, under which every update was a restart");
        context!.IsCollectible.Should().BeTrue();
        generation.Assembly.GetCustomAttributes<MeshNodeProviderAttribute>().Should().NotBeEmpty(
            "the attribute must be the PLATFORM's MeshNodeProviderAttribute — a second copy in the "
            + "module context would make the module's own attribute invisible to the platform");
        InMeshImpersonationGuard.IsInMeshAssembly(generation.Assembly).Should().BeFalse(
            "a module is first-party compiled code, classified with the platform, not as in-mesh code");

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IncompatibleModule>().Should().BeEmpty();
        provider.GetServices<InstalledModuleAssembly>()
            .Should().Contain(m => ReferenceEquals(m.Assembly, generation.Assembly));
        provider.GetRequiredService<ModuleContexts>().Should().BeSameAs(builder.ModuleContexts,
            "the registry is the mesh's singleton — the one NodeType builds and script sessions resolve through");
    }

    [Fact]
    public void AModuleInTheApplicationClosure_IsImageBound_AndAnythingElseIsNot()
    {
        ModuleContexts.IsImageBound(typeof(MeshBuilder).Assembly.GetName().Name!).Should().BeTrue(
            "a module in TRUSTED_PLATFORM_ASSEMBLIES is bound by name for every platform assembly — a "
            + "second copy in its own context would split its identity, so it stays in the default context");
        ModuleContexts.IsImageBound(Lib).Should().BeFalse();
    }

    // ───────────────────────────────────────────── two generations, and the unload

    [Fact]
    public async Task TwoGenerationsCoexist_TheNewOneServes_AndTheRetiredOneIsReallyCollected()
    {
        var unloads = new CollectibleContextUnloads();
        using var contexts = new ModuleContexts().Attach(unloads, null);

        var weakFirst = InstallSwapAndRetire(contexts, holdFirst: null);
        (await RetireStarted(contexts)).Should().BeTrue();

        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);
        outcome.Collected.Should().BeTrue($"nothing roots the retired generation ({outcome})");
        weakFirst.IsAlive.Should().BeFalse("a retired module generation must really be gone");
        CallVersion(contexts.Current(Lib)!).Should().Be(2, "the new generation keeps serving");
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL for the collection assertion above: hold ONE object of the retired
    /// generation and the drain must report that generation as RETAINED, BY NAME — so the "really
    /// collected" verdict is a measurement that can fail, not a check that passes on no evidence.
    /// </summary>
    [Fact]
    public async Task ARetiredGenerationSomethingStillHolds_IsReportedRetained_ByName()
    {
        var unloads = new CollectibleContextUnloads();
        using var contexts = new ModuleContexts().Attach(unloads, null);
        var held = new List<object>();

        var weakFirst = InstallSwapAndRetire(contexts, held);
        (await RetireStarted(contexts)).Should().BeTrue();

        var outcome = await CollectibleUnloadDrain.WaitUntilCollectedAsync(unloads);

        outcome.Collected.Should().BeFalse("an instance of the retired generation's type is still held");
        outcome.RetainedContexts.Should().ContainSingle(name => name.StartsWith(ModuleLoadContext.NamePrefix + Lib + "#"),
            "the retention must be NAMED — a leak nobody can attribute is a leak nobody fixes");
        weakFirst.IsAlive.Should().BeTrue();
        GC.KeepAlive(held);
    }

    // ───────────────────────────────────────────── dependents bind the current generation

    [Fact]
    public void ADependentModule_BindsItsDependencysCurrentGeneration_AndIsRecordedAsItsDependent()
    {
        var libBytes = Emit(Lib, LibSource(version: 1));
        var libPath = Write(Lib, "g1", libBytes);
        var depPath = Write(Dependent, "g1", Emit(Dependent, """
            [assembly: MeshWeaver.Test.LiveDependent.Module]
            namespace MeshWeaver.Test.LiveDependent;
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public static class Uses
            {
                public static int LibVersion() => MeshWeaver.Test.LiveLib.Api.Version;
                public static System.Reflection.Assembly LibAssembly() => typeof(MeshWeaver.Test.LiveLib.Api).Assembly;
            }
            """, MetadataReference.CreateFromImage(libBytes)));

        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "live"));
        builder.InstallAssemblies(libPath, depPath);

        var lib = builder.ModuleContexts.Current(Lib)!;
        var dep = builder.ModuleContexts.Current(Dependent)!;
        var uses = dep.Assembly.GetType("MeshWeaver.Test.LiveDependent.Uses")!;
        uses.GetMethod("LibVersion")!.Invoke(null, null).Should().Be(1);
        uses.GetMethod("LibAssembly")!.Invoke(null, null).Should().BeSameAs(lib.Assembly,
            "the dependent must bind the registry's CURRENT generation of the library — a private "
            + "copy would split every type that crosses between them");
        builder.ModuleContexts.DependentsOf(Lib).Select(g => g.Name).Should().Equal([Dependent],
            "a swap of the library has to take its dependents with it — the edge must be recorded");
    }

    // ───────────────────────────────────────────── a swap is staged, then published as ONE step

    /// <summary>
    /// #6128 review: a live swap used to make the module's N+1 current BEFORE re-binding its
    /// dependents, so every reader of the generations (the module endpoint table, the view
    /// registrations) saw N+1 while a dependent could still fail, and the rollback then unloaded a
    /// generation requests had been routed to. Staged, a dependent re-binds to the STAGED N+1 while
    /// nothing current moves; <see cref="ModuleContexts.CommitAll"/> publishes all of it with ONE
    /// <see cref="ModuleContexts.VersionChanged"/>.
    /// </summary>
    [Fact]
    public void AStagedSwap_RebindsTheDependentToN1_WhileNothingCurrentMoves_ThenCommitsInOneStep()
    {
        using var contexts = new ModuleContexts();
        var (libG2, depPath) = InstallLibAndDependent(contexts);
        var lib1 = Held(contexts, Lib);
        var dep1 = Held(contexts, Dependent);
        var versions = new List<long>();
        using var watch = contexts.VersionChanged.Subscribe(versions.Add);

        var stage = new ModuleSwapStage();
        var lib2 = contexts.LoadStaged(libG2, stage);
        stage.Add(lib2);
        var dep2 = contexts.LoadStaged(depPath, stage);
        stage.Add(dep2);

        LibVersionSeenBy(dep2).Should().Be(2,
            "a dependent loaded into the swap's stage must bind the STAGED library, not the serving one");
        contexts.Current(Lib).Should().BeSameAs(lib1, "nothing is current until the swap commits");
        contexts.Current(Dependent).Should().BeSameAs(dep1);
        versions.Should().BeEmpty("no reader of the generations may observe a swap that has not committed");

        contexts.CommitAll(stage);

        contexts.Current(Lib).Should().BeSameAs(lib2);
        contexts.Current(Dependent).Should().BeSameAs(dep2);
        versions.Should().ContainSingle("the whole swap is published as ONE step, never module-then-dependents");
    }

    [Fact]
    public void AStagedSwapThatIsDiscarded_LeavesTheServingGenerationsCurrent_AndPublishesNothing()
    {
        using var contexts = new ModuleContexts();
        var (libG2, depPath) = InstallLibAndDependent(contexts);
        var lib1 = Held(contexts, Lib);
        var dep1 = Held(contexts, Dependent);
        var versions = new List<long>();
        using var watch = contexts.VersionChanged.Subscribe(versions.Add);

        var stage = new ModuleSwapStage();
        stage.Add(contexts.LoadStaged(libG2, stage));
        stage.Add(contexts.LoadStaged(depPath, stage));
        contexts.DiscardStaged(stage);

        contexts.Current(Lib).Should().BeSameAs(lib1);
        contexts.Current(Dependent).Should().BeSameAs(dep1);
        LibVersionSeenBy(dep1).Should().Be(1, "the serving dependent never stopped binding the serving library");
        versions.Should().BeEmpty("a swap that failed before committing was never visible to any reader");
    }

    private (string LibG2, string DepPath) InstallLibAndDependent(ModuleContexts contexts)
    {
        var libBytes = Emit(Lib, LibSource(version: 1));
        var depPath = Write(Dependent, "g1", Emit(Dependent, """
            [assembly: MeshWeaver.Test.LiveDependent.Module]
            namespace MeshWeaver.Test.LiveDependent;
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public static class Uses
            {
                public static int LibVersion() => MeshWeaver.Test.LiveLib.Api.Version;
            }
            """, MetadataReference.CreateFromImage(libBytes)));
        contexts.Commit(contexts.Load(Write(Lib, "g1", libBytes)));
        var dep = contexts.Load(depPath);
        contexts.Commit(dep);
        LibVersionSeenBy(dep).Should().Be(1);
        return (Write(Lib, "g2", LibSource(version: 2)), depPath);
    }

    private static ModuleGeneration Held(ModuleContexts contexts, string module) =>
        contexts.Current(module) ?? throw new Xunit.Sdk.XunitException($"{module} is not held by the module registry");

    private static object? LibVersionSeenBy(ModuleGeneration dependent) =>
        dependent.Assembly.GetType("MeshWeaver.Test.LiveDependent.Uses") is { } uses
            && uses.GetMethod("LibVersion") is { } method
            ? method.Invoke(null, null)
            : throw new Xunit.Sdk.XunitException($"{dependent.Context.Name} carries no Uses.LibVersion");

    // ───────────────────────────────────────────── a generation that fails never displaces the serving one

    [Fact]
    public void AGenerationWhoseContributionsThrow_IsParked_AndTheServingGenerationStaysCurrent()
    {
        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "live"));
        builder.InstallAssemblies(Write(Lib, "g1", LibSource(version: 1)));
        var serving = builder.ModuleContexts.Current(Lib)!;

        builder.InstallAssemblies(Write(Lib, "g2", LibSource(version: 2, throwingNodes: true)));

        builder.ModuleContexts.Current(Lib).Should().BeSameAs(serving,
            "never a half-swapped state: a generation that cannot materialise its contributions must "
            + "leave the one that was serving in place");
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IncompatibleModule>().Should().ContainSingle(m => m.Name == Lib);
    }

    // ───────────────────────────────────────────── the incident shape: a NodeType bound to N+1's member

    /// <summary>
    /// The 2026-10-05 shape: a NodeType compiled against a member only the NEWER module generation
    /// has (<c>ThreadPreparation.Group</c>). While generation N serves, binding it fails exactly as
    /// production did — that half is the negative control. Once N+1 is current, a NodeType context
    /// resolves the module through the registry and the member binds, with no restart.
    /// </summary>
    [Fact]
    public void ANodeTypeCompiledAgainstTheNewerMember_BindsOnceTheNewerGenerationIsCurrent()
    {
        using var contexts = new ModuleContexts();
        var v2Bytes = Emit(Lib, LibSource(version: 2, withGroup: true));
        var nodePath = Write("DynamicNode_LiveProbe", "node", Emit("DynamicNode_LiveProbe", """
            public static class Probe { public static string Read() => MeshWeaver.Test.LiveLib.Api.Group; }
            """, MetadataReference.CreateFromImage(v2Bytes)));

        contexts.Commit(contexts.Load(Write(Lib, "g1", LibSource(version: 1))));
        Action onN = () => InvokeProbe(contexts, nodePath, "on-n");
        onN.Should().Throw<TargetInvocationException>(
                "generation N lacks the member — the incident's MissingMethodException shape, and the "
                + "proof this test can fail")
            .WithInnerException<MissingMemberException>();

        contexts.Commit(contexts.Load(Write(Lib, "g2", v2Bytes)));
        InvokeProbe(contexts, nodePath, "on-n1").Should().Be("grouped",
            "a NodeType built against N+1 must bind N+1 once it is current — in the running process");
    }

    // ───────────────────────────────────────────── a module's private dependency, for a context outside the modules

    /// <summary>
    /// A kernel script session is a context OUTSIDE the modules, and it compiles against a module's
    /// private closure (the file beside the module DLL). <see cref="ModuleContexts.ResolveDependency"/>
    /// is what it binds at run time: the entry assembly, or the private dependency loaded through the
    /// module's OWN context — the identity the module's code uses — each with a lease on that
    /// context; and nothing for a name the platform answers, a name nobody ships, or a version higher
    /// than the one shipped.
    /// </summary>
    [Fact]
    public void ResolveDependency_BindsAModulesPrivateDependency_ThroughTheModulesOwnContext()
    {
        using var contexts = new ModuleContexts();
        var generation = InstallWithPrivateDependency(contexts, "g1");

        var entry = contexts.ResolveDependency(new AssemblyName(PrivateModule));
        entry.Should().NotBeNull();
        entry!.Assembly.Should().BeSameAs(generation.Assembly);
        entry.Context.Should().BeSameAs(generation.Context);
        contexts.Leases.InFlight(generation.Context).Should().Be(1, "a binding comes with its lease");
        entry.Lease.Dispose();

        var dependency = contexts.ResolveDependency(new AssemblyName($"{PrivateDependency}, Version=6.2.0.0"));
        dependency.Should().NotBeNull("the module ships it beside its DLL — what the script compiled against");
        dependency!.Context.Should().BeSameAs(generation.Context, "it binds through the module's own context");
        AssemblyLoadContext.GetLoadContext(dependency.Assembly).Should().BeSameAs(generation.Context);
        generation.Assembly.GetType($"{PrivateModule}.Mailer")!.GetMethod("DependencyAssembly")!.Invoke(null, null)
            .Should().BeSameAs(dependency.Assembly, "ONE identity: the module's own code binds the same assembly");
        dependency.Lease.Dispose();
        var again = contexts.ResolveDependency(new AssemblyName(PrivateDependency))!;
        again.Assembly.Should().BeSameAs(dependency.Assembly, "once loaded, the module context's copy is the answer");
        again.Lease.Dispose();
        contexts.Leases.InFlight(generation.Context).Should().Be(0);

        contexts.ResolveDependency(new AssemblyName($"{PrivateDependency}, Version=7.0.0.0")).Should().BeNull(
            "a context may never answer a request with a lower version than the one asked for");
        contexts.ResolveDependency(new AssemblyName($"{PrivateModule}, Version=7.0.0.0")).Should().BeNull(
            "the entry assembly is held to the same version rule");
        contexts.ResolveDependency(typeof(MeshNode).Assembly.GetName()).Should().BeNull(
            "the platform is the default context's to answer");
        contexts.ResolveDependency(new AssemblyName("MeshWeaver.Test.NobodyShipsThis")).Should().BeNull();
        contexts.Leases.InFlight(generation.Context).Should().Be(0, "a refusal leaves no lease behind");
    }

    /// <summary>
    /// The session leases every module context it binds into until it ends — so a retiring
    /// generation waits for it — and releases them all when it does.
    /// </summary>
    [Fact]
    public void ModuleScriptBindings_LeaseTheBoundModuleUntilTheSessionEnds()
    {
        using var contexts = new ModuleContexts();
        var generation = InstallWithPrivateDependency(contexts, "g1");
        var bindings = MeshWeaver.Kernel.Hub.ModuleScriptBindings.PinCurrent(contexts);

        bindings.Bind(new AssemblyName(PrivateDependency)).Should().NotBeNull();
        bindings.Bind(new AssemblyName(PrivateModule)).Should().BeSameAs(generation.Assembly);
        contexts.Leases.InFlight(generation.Context).Should().Be(1, "one lease per bound context, held for the session");

        bindings.Dispose();
        contexts.Leases.InFlight(generation.Context).Should().Be(0, "the session's end releases it");
        bindings.Bind(new AssemblyName(PrivateModule)).Should().BeNull("an ended session binds nothing");
        contexts.Leases.InFlight(generation.Context).Should().Be(0);
    }

    /// <summary>
    /// Compile and run see the SAME generation: a session pinned to N never binds N+1 after a swap
    /// (it compiled against N), and never binds a displaced N it had not leased (N may already be
    /// unloading) — while a session pinned after the swap binds N+1. A generation the session HAD
    /// bound stays leased across the swap, so its retirement waits for the session.
    /// </summary>
    [Fact]
    public void ModuleScriptBindings_BindThePinnedGeneration_AndNeverOneASwapDisplacedBeforeTheBind()
    {
        using var contexts = new ModuleContexts();
        var n = InstallWithPrivateDependency(contexts, "g1");
        using var boundBeforeSwap = MeshWeaver.Kernel.Hub.ModuleScriptBindings.PinCurrent(contexts);
        using var unboundAtSwap = MeshWeaver.Kernel.Hub.ModuleScriptBindings.PinCurrent(contexts);
        boundBeforeSwap.Bind(new AssemblyName(PrivateModule)).Should().BeSameAs(n.Assembly);
        boundBeforeSwap.SessionAssemblies(new ServiceCollection().BuildServiceProvider())
            .Should().Contain(n.Assembly, "the session compiles against the generation it pinned");

        var n1 = InstallWithPrivateDependency(contexts, "g2");
        contexts.Current(PrivateModule).Should().BeSameAs(n1);

        boundBeforeSwap.Bind(new AssemblyName(PrivateDependency)).Should().BeNull(
            "the session compiled against N; N+1's copy would be the wrong generation, and N is displaced");
        unboundAtSwap.Bind(new AssemblyName(PrivateModule)).Should().BeNull(
            "N was displaced before this session leased it — it may be unloading; never bind it");
        contexts.Leases.InFlight(n.Context).Should().Be(1, "only the session that bound N before the swap holds it");
        contexts.Leases.InFlight(n1.Context).Should().Be(0, "a session pinned to N never touches N+1");

        using var afterSwap = MeshWeaver.Kernel.Hub.ModuleScriptBindings.PinCurrent(contexts);
        afterSwap.Bind(new AssemblyName(PrivateModule)).Should().BeSameAs(n1.Assembly, "a new session binds N+1");
    }

    private const string PrivateModule = "MeshWeaver.Test.PrivateDepLib";
    private const string PrivateDependency = "MeshWeaver.Test.PrivateDepOnly";

    private ModuleGeneration InstallWithPrivateDependency(ModuleContexts contexts, string generationName)
    {
        var dependencyBytes = Emit(PrivateDependency, $$"""
            [assembly: System.Reflection.AssemblyVersion("6.2.0.0")]
            namespace {{PrivateDependency}};
            public sealed class Envelope { }
            """);
        var modulePath = Write(PrivateModule, generationName, Emit(PrivateModule, $$"""
            [assembly: {{PrivateModule}}.Module]
            namespace {{PrivateModule}};
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public static class Mailer
            {
                public static System.Reflection.Assembly DependencyAssembly() => typeof({{PrivateDependency}}.Envelope).Assembly;
            }
            """, MetadataReference.CreateFromImage(dependencyBytes)));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(modulePath)!, PrivateDependency + ".dll"), dependencyBytes);
        var generation = contexts.Load(modulePath);
        contexts.Commit(generation);
        return generation;
    }

    // ───────────────────────────────────────────── helpers

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference InstallSwapAndRetire(ModuleContexts contexts, List<object>? holdFirst)
    {
        var first = contexts.Load(Write(Lib, "g1", LibSource(version: 1)));
        contexts.Commit(first);
        CallVersion(first).Should().Be(1);
        holdFirst?.Add(Activator.CreateInstance(first.Assembly.GetType("MeshWeaver.Test.LiveLib.Api+Token")!)!);

        var second = contexts.Load(Write(Lib, "g2", LibSource(version: 2)));
        var replaced = contexts.Commit(second);
        replaced.Should().BeSameAs(first, "the commit hands back the generation it replaced");
        first.Context.Should().NotBeSameAs(second.Context, "two generations, two contexts, side by side");
        CallVersion(second).Should().Be(2);
        pendingRetirement = first;
        return new WeakReference(first.Context);
    }

    private ModuleGeneration? pendingRetirement;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IObservable<bool> RetireStartedCore(ModuleContexts contexts)
    {
        var generation = pendingRetirement!;
        pendingRetirement = null;
        return contexts.Retire(generation, Budget);
    }

    private async Task<bool> RetireStarted(ModuleContexts contexts) =>
        await RetireStartedCore(contexts).Timeout(Budget).Await();

    private static int CallVersion(ModuleGeneration generation) =>
        (int)generation.Assembly.GetType("MeshWeaver.Test.LiveLib.Api")!
            .GetProperty("Version")!.GetValue(null)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string InvokeProbe(ModuleContexts contexts, string nodePath, string label)
    {
        var context = new NodeAssemblyLoadContext($"LiveProbe-{label}", nodePath, null, contexts);
        try
        {
            var assembly = context.LoadNodeAssembly()!;
            return (string)assembly.GetType("Probe")!.GetMethod("Read")!.Invoke(null, null)!;
        }
        finally
        {
            context.Dispose();
        }
    }

    private static string LibSource(int version, bool withGroup = false, bool throwingNodes = false) => $$"""
        [assembly: MeshWeaver.Test.LiveLib.Module]
        namespace MeshWeaver.Test.LiveLib;
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                {{(throwingNodes
                    ? "throw new System.InvalidOperationException(\"contributions of this generation cannot be built\")"
                    : "[new MeshWeaver.Mesh.MeshNode(\"LiveLib\") { Name = \"v" + version + "\" }]")}};
        }
        public static class Api
        {
            public static int Version => {{version}};
            {{(withGroup ? "public static string Group => \"grouped\";" : "")}}
            public sealed class Token { }
        }
        """;

    private static byte[] Emit(string assemblyName, string source, MetadataReference? extra = null)
    {
        var references = PlatformReferences.Platform();
        if (extra is not null)
            references = references.Add(extra);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    private string Write(string assemblyName, string generation, string source) =>
        Write(assemblyName, generation, Emit(assemblyName, source));

    /// <summary>Writes into <c>modules/&lt;name&gt;@&lt;generation&gt;/</c> — the landed layout.</summary>
    private string Write(string assemblyName, string generation, byte[] bytes)
    {
        var directory = Path.Combine(root, "modules", $"{assemblyName}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
