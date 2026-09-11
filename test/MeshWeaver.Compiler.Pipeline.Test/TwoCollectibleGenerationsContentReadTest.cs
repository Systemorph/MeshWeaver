using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 #3911's headline mechanism, put under an experiment instead of an inference — and the
/// answer is that the trap-door is ALREADY CLOSED at this seam.
///
/// <para><b>What #3911 claimed.</b> During the 2026-09-10 control-instance incident, two
/// collectible builds of <c>Hosting/InstanceAction</c> were alive in one pod; the node stream
/// materialized content with the NEWER build while the per-node hubs' watcher ran in the OLDER one,
/// so <c>node.ContentAs&lt;InstanceActionContent&gt;(…)</c> was said to return <c>null</c> —
/// "<c>null → Observable.Empty → silence</c>" — taking the InstanceAction control plane down with
/// no log line.</para>
///
/// <para><b>Why it needed re-measuring.</b> <c>ContentAsForeignAssemblyContentTest</c> already
/// pins the cross-assembly recovery, but with two statically compiled same-named types in ONE
/// non-collectible assembly. That is not the case #3911 describes: a COLLECTIBLE assembly takes a
/// different path through the serializer — <c>PolymorphicTypeInfoResolver</c> deliberately refuses
/// to auto-register a collectible type (it would poison the hub's registry and pin the context) and
/// formats the discriminator without registering it. Whether the recovery round-trip survives THAT
/// branch is the question, and it can only be answered by loading two genuine collectible
/// generations — which is what this test does.</para>
///
/// <para><b>Measured answer: it survives.</b> <see cref="ObjectAsExtensions"/>'s same-short-name
/// JSON round-trip recovers a value typed by another generation, in both directions, and a
/// DIFFERENTLY-named type still answers <c>null</c> so probe-dispatch call sites keep working. So a
/// cross-generation read is not where #3911's silence came from, and a fix aimed at
/// <c>ContentAs</c> would have changed nothing. This test exists so that conclusion is a standing,
/// falsifiable control rather than a claim in an issue thread: if the recovery ever regresses for
/// collectible generations, the silence #3911 describes becomes reachable again and this goes red.
/// </para>
/// </summary>
public class TwoCollectibleGenerationsContentReadTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string AssemblyName = "DynamicNode_Hosting_InstanceAction";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"two-generations-{Guid.NewGuid():N}");
    private readonly AssemblyLoadContext[] _contexts = new AssemblyLoadContext[2];

    public override async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
        {
            try { ctx?.Unload(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        await base.DisposeAsync();
    }

    /// <summary>
    /// The exact #3911 read: a watcher compiled in build A reads its own node's content, which the
    /// stream materialized in build B. Driven through the GENERIC <c>As&lt;T&gt;</c> — the overload
    /// production uses — by closing it over the runtime-emitted generation, so no second code path
    /// is being measured.
    /// </summary>
    [Fact]
    public void ContentTypedByAnotherCollectibleGenerationIsRecovered()
    {
        var buildA = LoadGeneration(index: 0);
        var buildB = LoadGeneration(index: 1);

        var typeA = buildA.GetType("InstanceActionContent")!;
        var typeB = buildB.GetType("InstanceActionContent")!;

        // Premise of the experiment. If any of this stops holding, the test is measuring something
        // other than the incident and must be rebuilt rather than trusted.
        typeA.Should().NotBeSameAs(typeB,
            "two collectible generations of one NodeType are two distinct CLR types");
        typeA.Name.Should().Be(typeB.Name,
            "a dynamic node assembly compiles without a namespace, so the bare names are identical "
            + "on both sides — which is exactly why `is T` / `as T` fails silently here");
        typeA.Assembly.IsCollectible.Should().BeTrue("this must be the COLLECTIBLE path, not a "
            + "second copy of the static-assembly case ContentAsForeignAssemblyContentTest covers");
        typeB.Assembly.IsCollectible.Should().BeTrue();

        var fromBuildB = Activator.CreateInstance(typeB)!;
        typeB.GetProperty("RequestedAction")!.SetValue(fromBuildB, "Restart");
        typeB.GetProperty("Attempt")!.SetValue(fromBuildB, 4);

        var node = MeshNode.FromPath("Deployments/memex-restart-20260910-1140") with
        {
            NodeType = "Hosting/InstanceAction",
            Content = fromBuildB,
        };

        // The trap-door the rule names, demonstrated: the older build's type does not match.
        typeA.IsInstanceOfType(node.Content).Should().BeFalse(
            "`node.Content is MyType` across two collectible generations is the silent-null "
            + "trap-door — this is the value every reader of this node receives");

        var recovered = ReadContentAs(node, typeA);

        recovered.Should().NotBeNull(
            "ContentAs must recover content typed by ANOTHER collectible generation of the same "
            + "NodeType — a null here is #3911's `null → Observable.Empty → silence`, which takes "
            + "an entire control plane down with no exception and no log line");
        recovered!.GetType().Should().BeSameAs(typeA,
            "the reader must get ITS OWN generation's type back, not the foreign one");
        typeA.GetProperty("RequestedAction")!.GetValue(recovered).Should().Be("Restart",
            "the recovery must carry the values across, not just produce an empty instance");
        typeA.GetProperty("Attempt")!.GetValue(recovered).Should().Be(4);
    }

    /// <summary>
    /// The OTHER direction — the newer build reading content the older one produced. A recompile
    /// wave leaves readers on both sides of the split, so a recovery that only worked one way
    /// would still strand half of them.
    /// </summary>
    [Fact]
    public void TheRecoveryWorksFromEitherGenerationTowardTheOther()
    {
        var buildA = LoadGeneration(index: 0);
        var buildB = LoadGeneration(index: 1);

        var typeA = buildA.GetType("InstanceActionContent")!;
        var typeB = buildB.GetType("InstanceActionContent")!;

        var fromBuildA = Activator.CreateInstance(typeA)!;
        typeA.GetProperty("RequestedAction")!.SetValue(fromBuildA, "Sample");

        var node = MeshNode.FromPath("Deployments/memex-sample-20260910-1156") with
        {
            NodeType = "Hosting/InstanceAction",
            Content = fromBuildA,
        };

        var recovered = ReadContentAs(node, typeB);

        recovered.Should().NotBeNull(
            "the recovery must be symmetric — a recompile wave leaves readers on BOTH sides of the "
            + "generation split, and either side reading null is the same silence");
        recovered!.GetType().Should().BeSameAs(typeB);
        typeB.GetProperty("RequestedAction")!.GetValue(recovered).Should().Be("Sample");
    }

    /// <summary>
    /// 🚨 The control in the opposite direction. Recovery is for the SAME short name only: a
    /// differently-named type must still answer <c>null</c>, because call sites probe-dispatch on
    /// exactly that (<c>x.As&lt;Index&gt;() ?? treat x as the item itself</c>) and shape-recovering
    /// across unrelated types once broke every API-token login. Without this, "make ContentAs
    /// recover more" would pass the tests above while re-opening a worse hole.
    /// </summary>
    [Fact]
    public void ADifferentlyNamedTypeFromAnotherGenerationStaysNull()
    {
        var buildA = LoadGeneration(index: 0);
        var buildB = LoadGeneration(index: 1);

        var unrelated = buildB.GetType("InstanceActionIndex")!;
        var fromBuildB = Activator.CreateInstance(unrelated)!;
        unrelated.GetProperty("RequestedAction")!.SetValue(fromBuildB, "Roll");

        var node = MeshNode.FromPath("Deployments/memex-logs-20260910-1153") with
        {
            NodeType = "Hosting/InstanceAction",
            Content = fromBuildB,
        };

        var recovered = ReadContentAs(node, buildA.GetType("InstanceActionContent")!);

        recovered.Should().BeNull(
            "recovery is for the SAME short name only — a differently-named typed content must "
            + "stay null so probe-dispatch call sites fall through to their next interpretation, "
            + "even when its shape happens to fit");
    }

    /// <summary>
    /// Reads <c>node.Content</c> as <paramref name="target"/> through the GENERIC
    /// <c>ObjectAsExtensions.As&lt;T&gt;</c> closed over a runtime-emitted type — the same overload
    /// <c>MeshNodeContentExtensions.ContentAs&lt;T&gt;</c> forwards to, so the branch under test is
    /// the production one rather than the runtime-Type sibling.
    /// </summary>
    private object? ReadContentAs(MeshNode node, Type target)
    {
        var options = GetHost().JsonSerializerOptions;
        var generic = typeof(ObjectAsExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(ObjectAsExtensions.As) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(target);
        return generic.Invoke(null, [node.Content, options, null, node.Path]);
    }

    /// <summary>
    /// Emits one generation of a namespace-less, NodeType-shaped assembly into its own never-reused
    /// directory and loads it into its own COLLECTIBLE context — the layout and the load
    /// <c>EmitToDiskWithRetry</c> + <c>CompilationCacheService</c> produce for every recompile.
    /// </summary>
    private Assembly LoadGeneration(int index)
    {
        if (_contexts[index] is not null)
            return _contexts[index].Assemblies.First();

        Directory.CreateDirectory(_root);
        var dir = Path.Combine(_root, $"{AssemblyName}_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, $"{AssemblyName}.dll");

        var compilation = CSharpCompilation.Create(
            assemblyName: AssemblyName,
            syntaxTrees:
            [
                CSharpSyntaxTree.ParseText(
                    "public sealed class InstanceActionContent { public string RequestedAction { get; set; } = \"\"; public int Attempt { get; set; } }"
                    + " public sealed class InstanceActionIndex { public string RequestedAction { get; set; } = \"\"; }")
            ],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emitted = compilation.Emit(dll);
        emitted.Success.Should().BeTrue(
            string.Join("; ", emitted.Diagnostics.Select(d => d.ToString())));

        var context = new AssemblyLoadContext($"{AssemblyName}-{index}", isCollectible: true);
        _contexts[index] = context;
        return context.LoadFromAssemblyPath(dll);
    }
}
