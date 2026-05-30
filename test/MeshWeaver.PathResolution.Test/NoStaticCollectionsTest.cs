using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Architecture guard for the absolute rule "🚨 No static collections — ever"
/// (see <c>Doc/Architecture/NoStaticState.md</c>). Reflects over every
/// <c>MeshWeaver.*</c> assembly in the test output and fails if any <c>static</c>
/// field is a mutable collection / cache type. This is the active replacement for
/// the hand-maintained <c>KnownStaticCaches</c> reflection watchdog that used to
/// live in <c>MonolithMeshTestBase</c>: a build-time gate instead of a runtime trend.
///
/// <para>Every hit must either be eliminated (caches → mesh-scoped instance singletons
/// registered in <c>MeshBuilder</c>, lifetime = mesh) or, if it is a genuinely-immutable
/// constant lookup, listed in <see cref="Allowed"/> with a one-word reason. The
/// <c>CACHE</c>-tagged entries are the burn-down list — driving them to zero is what
/// unblocks <c>maxParallelThreads &gt; 1</c> in <c>xunit.runner.json</c>.</para>
/// </summary>
public class NoStaticCollectionsTest
{
    /// <summary>Fully-qualified type-name prefixes that denote MUTABLE collection / cache
    /// state. Immutable / Frozen collection types are intentionally absent — they are allowed.</summary>
    private static readonly string[] MutableCollectionTypePrefixes =
    {
        "System.Collections.Generic.Dictionary`",
        "System.Collections.Generic.List`",
        "System.Collections.Generic.HashSet`",
        "System.Collections.Generic.Queue`",
        "System.Collections.Generic.Stack`",
        "System.Collections.Generic.SortedDictionary`",
        "System.Collections.Generic.SortedSet`",
        "System.Collections.Concurrent.ConcurrentDictionary`",
        "System.Collections.Concurrent.ConcurrentBag`",
        "System.Collections.Concurrent.ConcurrentQueue`",
        "System.Collections.Concurrent.ConcurrentStack`",
        "Microsoft.Extensions.Caching.Memory.MemoryCache",
    };

    /// <summary>
    /// Sanctioned static collection fields: <c>"Namespace.Type.Field" =&gt; reason</c>.
    /// <c>CONST</c> = immutable, write-once constant lookup (out of scope — never mutated at runtime).
    /// <c>MEMO</c>  = process-global memoization keyed by Type/MethodInfo (pure-by-key; migrate when touched).
    /// <c>CACHE</c> = mutable mesh/runtime state — MUST migrate to a mesh-scoped instance; drive these to ZERO.
    /// When the last <c>CACHE</c> line is gone we can flip <c>maxParallelThreads &gt; 1</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>
    {
        // ---- CONST: immutable, write-once constant lookups (allowed) ----
        ["MeshWeaver.AI.AccessContextAIFunction.TimeoutExemptTools"] = "CONST",
        ["MeshWeaver.AI.AgentChatClient.BinaryExtensions"] = "CONST",
        ["MeshWeaver.AI.AgentChatClient.ExtensionToMediaType"] = "CONST",
        ["MeshWeaver.AI.AgentContext.StandardPrefixes"] = "CONST",
        ["MeshWeaver.ContentCollections.ContentCollection.MimeTypes"] = "CONST",
        ["MeshWeaver.ContentCollections.DocSharpContentTransformer.Extensions"] = "CONST",
        ["MeshWeaver.Hosting.Persistence.IncludedPartitionStaticProvider.ReservedNames"] = "CONST",
        ["MeshWeaver.Layout.ComboboxControl+ReplaceMethodsAttribute.MethodMap"] = "CONST",
        ["MeshWeaver.Layout.Domain.DataModelLayoutArea.ExcludedMethods"] = "CONST",
        ["MeshWeaver.Layout.EditorExtensions.BasicControls"] = "CONST",
        ["MeshWeaver.Layout.EditorExtensions.ListControls"] = "CONST",
        ["MeshWeaver.Layout.EditorExtensions.SpecialControls"] = "CONST",
        ["MeshWeaver.Markdown.LayoutAreaMarkdownParser.BreakChars"] = "CONST",
        ["MeshWeaver.Markdown.LayoutAreaMarkdownParser.DirectPathBreakChars"] = "CONST",
        ["MeshWeaver.Markdown.LayoutAreaMarkdownParser.EndTokenChars"] = "CONST",
        ["MeshWeaver.Markdown.LayoutAreaMarkdownParser.IgnoreChars"] = "CONST",
        ["MeshWeaver.Markdown.LayoutAreaMarkdownParser.ReservedKeywords"] = "CONST",
        ["MeshWeaver.Mesh.Completion.UnifiedReferenceAutocompleteProvider.Keywords"] = "CONST",
        ["MeshWeaver.Mesh.PartitionDefinition.NodeTypeToSuffix"] = "CONST",
        ["MeshWeaver.Mesh.QueryParser.ReservedQualifiers"] = "CONST",
        ["MeshWeaver.Mesh.Security.PermissionEvaluator.BuiltInRoles"] = "CONST",
        ["MeshWeaver.Messaging.MessageHubPluginExtensions.HandlerTypes"] = "CONST",
        ["MeshWeaver.Messaging.MessageService.ExcludedFromLogging"] = "CONST",
        ["MeshWeaver.Reflection.TypeExtensions.IntegerTypes"] = "CONST",
        ["MeshWeaver.Reflection.TypeExtensions.RealTypes"] = "CONST",

        // ---- MEMO: process-global memoization, pure-by-key (Type/MethodInfo/content) ----
        ["MeshWeaver.Hosting.Security.AccessControlPipeline.AttributeCache"] = "MEMO: Type -> attribute",
        ["MeshWeaver.Markdown.MarkdownExtensions.PipelineCache"] = "MEMO: (lang,style) -> pipeline",
        ["MeshWeaver.Messaging.MessageHubConfiguration._systemMessageCache"] = "MEMO: Type -> bool",
        ["MeshWeaver.Reflection.GenericCaches.MethodCaches"] = "MEMO: MethodInfo",
        ["MeshWeaver.Reflection.GenericCaches.TypeCaches"] = "MEMO: Type",

        // ---- PROC: tied to a process-global resource; per-mesh is meaningless, no test bleed ----
        ["MeshWeaver.Kernel.Hub.KernelExecutor._probingDirs"] = "PROC: AssemblyLoadContext.Default.Resolving probe registry (one resolver per process)",

        // ---- CACHE: mutable mesh/runtime state — MUST migrate to mesh-scoped instance. BURN DOWN TO ZERO. ----
        ["MeshWeaver.Graph.Configuration.NodeTypeRegistry.Nodes"] = "CACHE: deleted in source — drops on Graph rebuild",
        ["MeshWeaver.Graph.Configuration.ApiTokenNodeType.ValidationCache"] = "CACHE: -> mesh-scoped IMemoryCache (10-min TTL)",
        ["MeshWeaver.Hosting.Monolith.TestBase.MonolithMeshTestBase._sharedProviders"] = "CACHE: shared DI providers across tests (opt-in only; -> IClassFixture)",
        ["MeshWeaver.Layout.EditorExtensions.InitializedEditStates"] = "CACHE: -> per-layout-area state",
        ["MeshWeaver.Fixture.XUnitFileOutputRegistry._activeOutputHelpers"] = "CACHE: test-infra review",
    };

    [Fact]
    public void No_static_mutable_collection_fields_outside_allowlist()
    {
        var offenders = new List<string>();

        foreach (var asm in LoadMeshWeaverAssemblies())
        {
            foreach (var type in SafeGetTypes(asm))
            {
                if (type is null) continue;
                // Skip compiler-generated closures/state machines.
                if (type.Name.Contains('<') || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    continue;

                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsLiteral) continue; // const
                    var ftName = field.FieldType.FullName ?? field.FieldType.Name;
                    if (!MutableCollectionTypePrefixes.Any(p => ftName.StartsWith(p, StringComparison.Ordinal)))
                        continue;
                    offenders.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        var unexpected = offenders
            .Where(o => !Allowed.ContainsKey(o))
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();

        unexpected.Should().BeEmpty(
            "static collections are forbidden (NoStaticState.md) — each must become a mesh-scoped instance " +
            "singleton, or be added to Allowed with a CONST/MEMO/CACHE reason. Found:\n  " +
            string.Join("\n  ", unexpected));
    }

    private static IEnumerable<Assembly> LoadMeshWeaverAssemblies()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var dll in Directory.EnumerateFiles(dir, "MeshWeaver.*.dll"))
        {
            Assembly? asm = null;
            try { asm = Assembly.LoadFrom(dll); }
            catch { /* native / unloadable — skip */ }
            if (asm is not null) yield return asm;
        }
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types; }
        catch { return Array.Empty<Type?>(); }
    }
}
