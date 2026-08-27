using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Every module a host DECLARES must be one an instance can actually end up with — either because
/// this repo's publish puts it in the image, or because it is registry-delivered AND declared
/// <c>Modules:Required</c> so its absence is LOUD.
///
/// <para>🚨 <b>The failure this exists to stop is silent, green, and total.</b>
/// <c>Modules:Assemblies</c> is skip-and-continue by design: a listed module that does not resolve
/// is skipped with one stderr line, because a host that will not start is worse than one missing a
/// feature (that rule is what ended the 3.0.0-rc5 boot loop). The cost is that an entry naming a
/// module nothing ships is indistinguishable, at runtime, from an entry naming one that loaded —
/// the pods come up, readiness passes, the rollout is green, and the feature is simply gone.
/// rc5 shipped an image missing FOURTEEN extracted modules while appsettings still listed them.</para>
///
/// <para><c>Modules:Required</c> is the existing cure — it makes the required-modules health check
/// Unhealthy, so readiness fails and the rollout STALLS while the pods that still have the module
/// keep serving. But nothing until now checked that a declaration is BACKED by anything, so the
/// cure had to be remembered at exactly the moment it is easiest to forget: while deleting the
/// project that used to supply the module.</para>
///
/// <para><b>The invariant, which the tree already satisfied before this test existed</b> — it
/// describes the deliberate design rather than imposing a new one. Every entry is either:</para>
/// <list type="bullet">
/// <item>SHIPPED — a project in this repo produces it AND the host's publish reaches it (through
/// the ProjectReference closure, or the <c>modules/&lt;Name&gt;/</c> lane in
/// MeshModulesPublish.targets); or</item>
/// <item>REGISTRY-DELIVERED — no project here produces it, so an instance may genuinely not have
/// it, and it is declared <c>Required</c>. The five Blazor/Speech packs that left for the registry
/// (Plugins #570) are all in this state.</item>
/// </list>
///
/// <para>Anything else is a declaration nothing backs, and this test names it.</para>
/// </summary>
public class ModuleDeclarationIntegrityTest
{
    /// <summary>Hosts that declare modules, mapped to the project whose publish builds the image.</summary>
    private static readonly (string Settings, string Project)[] Hosts =
    [
        // 🚨 Memex.Portal.Distributed and Memex.Portal.Monolith are NOT absent by oversight: they
        // moved to MeshWeaver.Plugins with the GUI extraction, so this repo no longer has their
        // appsettings to check. The guard must follow its subject — an equivalent over those two
        // hosts belongs in that repo, and until it exists their module declarations are unguarded.
        // Listing them here instead would fail on a missing file and say nothing about the
        // declarations, which is the failure that brought this comment about.
        ("memex/Memex.LocalMesh/appsettings.json",
         "memex/Memex.LocalMesh/Memex.LocalMesh.csproj"),
    ];

    [Fact]
    public void EveryDeclaredModule_IsEitherShippedByThisRepo_OrDeclaredRequired()
    {
        var root = FindRepoRoot();
        var problems = new List<string>();

        foreach (var (settingsPath, projectPath) in Hosts)
        {
            var settings = Path.Combine(root, settingsPath);
            var project = Path.Combine(root, projectPath);
            Assert.True(File.Exists(settings), $"{settingsPath} not found — update this test's Hosts list.");
            Assert.True(File.Exists(project), $"{projectPath} not found — update this test's Hosts list.");

            var (assemblies, required) = ReadModuleLists(settings);
            if (assemblies.Count == 0 && required.Count == 0)
                continue;

            var closure = ProjectReferenceClosure(project);
            var lane = ModuleLaneAssemblies(root, project);
            var laneIsLoadBearing = false;

            foreach (var entry in assemblies.Concat(required).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? entry[..^4]
                    : entry;

                var producedHere = FindProject(root, name) is not null;
                var inClosure = closure.Contains(name, StringComparer.OrdinalIgnoreCase);
                var inLane = lane.Contains(name, StringComparer.OrdinalIgnoreCase);
                var reachable = inClosure || inLane;
                var isRequired = required.Contains(entry, StringComparer.OrdinalIgnoreCase);

                // The lane is the ONLY thing shipping this entry, which makes the host's import of
                // the lane load-bearing — asserted after this loop.
                if (producedHere && inLane && !inClosure && !isRequired)
                    laneIsLoadBearing = true;

                if (isRequired || (producedHere && reachable))
                    continue;

                problems.Add(producedHere
                    ? $"{settingsPath}: '{entry}' names a project that EXISTS in this repo "
                      + $"(src/{name}) but nothing ships it into this host's image — it is not in the "
                      + "ProjectReference closure and not in the modules/ lane "
                      + "(memex/MeshModulesPublish.targets). Listed under Modules:Assemblies it will be "
                      + "SKIPPED silently and the feature will be absent from a GREEN rollout. Either "
                      + "restore the reference / add it to MeshModule(Closure), or move it to "
                      + "Modules:Required so the health check stalls the rollout instead."
                    : $"{settingsPath}: '{entry}' names a module NO project in this repo produces, so it "
                      + "can only arrive from the registry — which means an instance may not have it. "
                      + "Listed under Modules:Assemblies its absence is SILENT: skip-and-continue, green "
                      + "rollout, feature gone. Move it to Modules:Required (the required-modules health "
                      + "check then reports Unhealthy and readiness holds the rollout), or delist it.");
            }

            // 🚨 Declaring a module on the closure lane ships NOTHING unless the host imports the
            // lane worker: PublishMeshModules and LayoutMeshModuleClosuresAfterBuild hang off THAT
            // import, so without it modules/ is never created and every lane-only entry is skipped
            // at load — silently, on a green build. Memex.LocalMesh sat in exactly that state: the
            // import lived in the two portal hosts and left with them for MeshWeaver.Plugins
            // (83356b3d5), so the AI engine had to be wired as a ProjectReference riding the app
            // closure instead (8dd8eeecf). Inventory membership is not shipping.
            if (laneIsLoadBearing && !ImportsModuleLane(project))
                problems.Add($"{projectPath}: modules are declared on the CLOSURE lane for this host, "
                    + "but the project does not import memex/MeshModulesPublish.targets — so the lane "
                    + "never runs, modules/ is never laid out, and every lane-only entry above is "
                    + "SKIPPED at load on a green build. Add the Import element (path relative to "
                    + "the host) alongside the MeshModulesClosureSubset that names them.");
        }

        Assert.True(problems.Count == 0,
            "Module declarations that nothing backs — each would ship a green rollout missing the "
            + "feature it names:\n  - " + string.Join("\n  - ", problems));
    }

    /// <summary>
    /// Whether the host imports the module-lane worker. Without it the lane's targets are not in
    /// this project's build at all, and the entire modules/ layout silently does not happen.
    /// </summary>
    private static bool ImportsModuleLane(string projectPath) =>
        System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(projectPath),
            @"<Import\s+Project=""[^""]*MeshModulesPublish\.targets");

    /// <summary>
    /// Assembly names reachable from <paramref name="projectPath"/> through ProjectReference, i.e.
    /// what the app closure puts beside the host. Assembly name is taken as the project file name,
    /// which is this repo's convention throughout.
    /// </summary>
    private static HashSet<string> ProjectReferenceClosure(string projectPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(projectPath));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!File.Exists(current) || !seen.Add(Path.GetFileNameWithoutExtension(current)))
                continue;

            var dir = Path.GetDirectoryName(current)!;
            foreach (var reference in ProjectReferences(current))
            {
                var resolved = Path.GetFullPath(Path.Combine(dir, reference.Replace('\\', Path.DirectorySeparatorChar)));
                pending.Push(resolved);
            }
        }

        return seen;
    }

    private static IEnumerable<string> ProjectReferences(string projectPath) =>
        System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(projectPath), """<ProjectReference\s+Include="([^"]+)["]""")
            .Select(m => m.Groups[1].Value);

    /// <summary>
    /// Assembly names laid into <c>modules/&lt;Name&gt;/</c> for THIS host — the lane that ships a
    /// module whose ProjectReference has been flipped off.
    ///
    /// <para>🚨 Per host, not global. The inventory in MeshModulesPublish.targets is shared, but a
    /// host may narrow the CLOSURE half of it with <c>MeshModulesClosureSubset</c> (the targets
    /// remove every <c>@(MeshModuleClosure)</c> the subset does not name). Reading the inventory
    /// globally would credit a host with modules its own publish drops — a guard passing on
    /// evidence that belongs to a different host. The THIN lane (<c>@(MeshModule)</c>) is not
    /// narrowed by the subset, so only the closure half is filtered.</para>
    ///
    /// <para>No host sets a subset today, so this is closing the hole before it opens rather than
    /// fixing a live miss.</para>
    /// </summary>
    private static HashSet<string> ModuleLaneAssemblies(string root, string hostProjectPath)
    {
        var targets = Path.Combine(root, "memex", "MeshModulesPublish.targets");
        if (!File.Exists(targets))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var text = File.ReadAllText(targets);

        var thin = Names(text, """<MeshModule\s+Include="([^"]+)["]""");
        var closure = Names(text, """<MeshModuleClosure\s+Include="([^"]+)["]""");

        var subset = ClosureSubset(hostProjectPath);
        if (subset.Count > 0)
            closure.IntersectWith(subset);

        thin.UnionWith(closure);
        return thin;

        static HashSet<string> Names(string text, string pattern) =>
            System.Text.RegularExpressions.Regex
                .Matches(text, pattern)
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The host's <c>MeshModulesClosureSubset</c>, split on <c>;</c>. Empty when unset, which the
    /// targets treat as "the full inventory".
    /// </summary>
    private static HashSet<string> ClosureSubset(string hostProjectPath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(hostProjectPath),
            "<MeshModulesClosureSubset>([^<]*)</MeshModulesClosureSubset>");

        return match.Success
            ? match.Groups[1].Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindProject(string root, string assemblyName)
    {
        foreach (var area in (string[])["src", "memex"])
        {
            var dir = Path.Combine(root, area);
            if (!Directory.Exists(dir))
                continue;

            var hit = Directory
                .EnumerateFiles(dir, $"{assemblyName}.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (hit is not null)
                return hit;
        }

        return null;
    }

    /// <summary>Reads the two lists, tolerating the comments these appsettings files carry.</summary>
    private static (List<string> Assemblies, List<string> Required) ReadModuleLists(string settingsPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(settingsPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty("Modules", out var modules))
            return ([], []);

        return (Read(modules, "Assemblies"), Read(modules, "Required"));

        static List<string> Read(JsonElement modules, string name) =>
            modules.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)]
                : [];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
