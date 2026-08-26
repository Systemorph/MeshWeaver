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
        ("memex/aspire/Memex.Portal.Distributed/appsettings.json",
         "memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj"),
        ("memex/Memex.Portal.Monolith/appsettings.json",
         "memex/Memex.Portal.Monolith/Memex.Portal.Monolith.csproj"),
        ("memex/Memex.LocalMesh/appsettings.json",
         "memex/Memex.LocalMesh/Memex.LocalMesh.csproj"),
    ];

    [Fact]
    public void EveryDeclaredModule_IsEitherShippedByThisRepo_OrDeclaredRequired()
    {
        var root = FindRepoRoot();
        var lane = ModuleLaneAssemblies(root);
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

            foreach (var entry in assemblies.Concat(required).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? entry[..^4]
                    : entry;

                var producedHere = FindProject(root, name) is not null;
                var reachable = closure.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || lane.Contains(name, StringComparer.OrdinalIgnoreCase);
                var isRequired = required.Contains(entry, StringComparer.OrdinalIgnoreCase);

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
        }

        Assert.True(problems.Count == 0,
            "Module declarations that nothing backs — each would ship a green rollout missing the "
            + "feature it names:\n  - " + string.Join("\n  - ", problems));
    }

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
    /// Assembly names laid into <c>modules/&lt;Name&gt;/</c> by the publish targets — the lane that
    /// ships a module whose ProjectReference has been flipped off.
    /// </summary>
    private static HashSet<string> ModuleLaneAssemblies(string root)
    {
        var targets = Path.Combine(root, "memex", "MeshModulesPublish.targets");
        if (!File.Exists(targets))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(targets), """<MeshModule(?:Closure)?\s+Include="([^"]+)["]""")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
