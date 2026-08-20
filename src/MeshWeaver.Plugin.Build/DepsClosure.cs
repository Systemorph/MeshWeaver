using System.Text.Json;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Derives a module's PRIVATE dependency closure from its own <c>&lt;Module&gt;.deps.json</c> —
/// the assemblies the module needs at runtime that the PLATFORM does not carry.
///
/// <para><b>Why this exists.</b> When a module's source left the platform repo (#1882), its
/// package dependencies left the app closure with it — and the bundle lane packed the entry DLL
/// alone, so every landed module faulted at first use on whichever private dependency boot touched
/// first (<c>Microsoft.Extensions.AI.OpenAI</c> on chat, <c>Microsoft.Graph</c> at host start,
/// 2026-08-19/20 memex outage). Hand-naming each dependency with <c>--with</c> is whack-a-mole
/// across 12 modules; pinning the dependency into the platform instead (#1912) ships 43 MB of
/// Graph/Kiota to deployments that send no mail — the exact cost the module split exists to avoid.
/// The bundle must carry the module's OWN dependencies, derived, not remembered.</para>
///
/// <para><b>The rule.</b> The deps.json target graph is walked twice from the module's direct
/// dependencies:</para>
/// <list type="bullet">
/// <item><b>Platform-reachable</b>: everything transitively reachable from the module's
/// MeshWeaver.* references (project references to the framework checkout, or MeshWeaver.*
/// packages). Those assemblies ship in the consumer's <c>/app</c> by construction — bundling them
/// would ship the platform inside the module, and the default load context resolves the app's copy
/// first anyway.</item>
/// <item><b>Own</b>: everything transitively reachable from the module's NON-MeshWeaver package
/// references, MINUS the platform-reachable set. These are exactly the assemblies present nowhere
/// but beside the module — the private closure the bundle must carry.</item>
/// </list>
///
/// <para>Shared-framework assemblies (<c>FrameworkReference</c>) never appear as package nodes in
/// deps.json, so they are excluded by construction. A diamond (a package reachable from both
/// sides) is platform-carried and excluded — the versions agree because platform and modules
/// restore from the same central package pins.</para>
///
/// <para>Pure text-in/data-out so the derivation is unit-testable with no build output on disk;
/// <see cref="ModulePackCommand"/> resolves the derived file names against the module folder and
/// refuses a missing file (the build must run with <c>CopyLocalLockFileAssemblies=true</c> for the
/// package assemblies to be present).</para>
/// </summary>
public static class DepsClosure
{
    /// <summary>The derived closure: runtime file names to bundle beside the entry DLL, plus the
    /// platform-carried names excluded (diagnostic — printed so a pack log shows the split), plus
    /// warnings for nodes with native <c>runtimeTargets</c> the bundle does not carry.</summary>
    public sealed record Result(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> ExcludedPlatformCarried,
        IReadOnlyList<string> Warnings);

    private sealed record Node(
        string Name,
        List<string> Dependencies,
        List<string> RuntimeFiles,
        bool HasNativeAssets,
        string? LibraryType);

    /// <summary>
    /// Derives the private closure from the deps.json text of <paramref name="moduleName"/>.
    /// Throws <see cref="InvalidDataException"/> on a deps.json that lacks the module's own node —
    /// a derivation from the wrong file must be a refusal, never an empty closure that packs a
    /// module which faults at first use.
    /// </summary>
    public static Result Derive(string depsJsonText, string moduleName)
    {
        using var document = JsonDocument.Parse(depsJsonText);
        var root = document.RootElement;

        // The target section: prefer the runtimeTarget's own name (present in every SDK-emitted
        // deps.json), else the first target — never a RID-agnostic guess.
        string? targetName = null;
        if (root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            && runtimeTarget.TryGetProperty("name", out var rtName))
            targetName = rtName.GetString();
        if (!root.TryGetProperty("targets", out var targets))
            throw new InvalidDataException("deps.json has no 'targets' section");
        JsonElement target = default;
        var found = false;
        foreach (var candidate in targets.EnumerateObject())
        {
            if (targetName is null || candidate.Name == targetName)
            {
                target = candidate.Value;
                found = true;
                break;
            }
        }
        if (!found)
            throw new InvalidDataException(
                $"deps.json target '{targetName}' not found among its targets");

        // libraries: name/version -> { type: package|project|... } — the node kind.
        var libraryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libraries))
            foreach (var lib in libraries.EnumerateObject())
                if (lib.Value.TryGetProperty("type", out var type))
                    libraryTypes[NameOf(lib.Name)] = type.GetString() ?? "";

        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target.EnumerateObject())
        {
            var name = NameOf(entry.Name);
            var dependencies = new List<string>();
            if (entry.Value.TryGetProperty("dependencies", out var deps))
                dependencies.AddRange(deps.EnumerateObject().Select(d => d.Name));
            var runtimeFiles = new List<string>();
            if (entry.Value.TryGetProperty("runtime", out var runtime))
                runtimeFiles.AddRange(runtime.EnumerateObject()
                    .Select(r => Path.GetFileName(r.Name.Replace('\\', '/')))
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            var hasNative = entry.Value.TryGetProperty("runtimeTargets", out var rts)
                            && rts.EnumerateObject().Any();
            nodes[name] = new Node(name, dependencies, runtimeFiles, hasNative,
                libraryTypes.GetValueOrDefault(name));
        }

        if (!nodes.TryGetValue(moduleName, out var module))
            throw new InvalidDataException(
                $"deps.json carries no node for '{moduleName}' — wrong file, or the module was "
                + "renamed after the build");

        // The split at the module's DIRECT references. A MeshWeaver.* reference — project or
        // package — roots the platform side; everything else roots the module's own side.
        var platformRoots = module.Dependencies.Where(IsPlatform).ToList();
        var ownRoots = module.Dependencies.Where(d => !IsPlatform(d)).ToList();

        var platformReachable = Reach(nodes, platformRoots);
        var ownReachable = Reach(nodes, ownRoots);

        var files = new List<string>();
        var excluded = new List<string>();
        var warnings = new List<string>();
        foreach (var name in ownReachable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (!nodes.TryGetValue(name, out var node) || node.RuntimeFiles.Count == 0)
                continue;
            // Safety net: a MeshWeaver.* assembly must never ride in a module bundle, whatever
            // path reached it — landing beside the module would shadow the platform's own binary.
            if (IsPlatform(name))
                continue;
            if (platformReachable.Contains(name))
            {
                excluded.Add(name);
                continue;
            }
            files.AddRange(node.RuntimeFiles);
            if (node.HasNativeAssets)
                warnings.Add(
                    $"'{name}' declares native runtimeTargets the bundle does not carry — "
                    + "if the module needs them at runtime, they must ship another way");
        }

        return new Result(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            excluded,
            warnings);
    }

    private static bool IsPlatform(string name) =>
        name.StartsWith("MeshWeaver.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MeshWeaver", StringComparison.OrdinalIgnoreCase);

    /// <summary>Transitive reachability over the dependency edges, from <paramref name="roots"/>
    /// inclusive. Missing nodes (framework-supplied names deps.json lists as dependencies but
    /// carries no entry for) are simply absent — nothing to bundle, nothing to walk.</summary>
    private static HashSet<string> Reach(
        IReadOnlyDictionary<string, Node> nodes, IEnumerable<string> roots)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!reached.Add(name) || !nodes.TryGetValue(name, out var node))
                continue;
            foreach (var dependency in node.Dependencies)
                queue.Enqueue(dependency);
        }
        return reached;
    }

    /// <summary>"Name/version" → "Name" (deps.json keys carry the resolved version).</summary>
    private static string NameOf(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }
}
