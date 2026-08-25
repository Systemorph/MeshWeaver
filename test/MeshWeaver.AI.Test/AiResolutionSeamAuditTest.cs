#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Architecture audit: every place that RESOLVES skills, agents or models goes through the one
/// seam (<see cref="AiSourceCatalog"/> via <see cref="AiSettingsNodeType"/>), and no hard-coded
/// resolution query creeps back in outside it. Two halves:
/// <list type="number">
///   <item>the known entry points must reference the seam (a refactor that reverts one to a literal
///     fails here, not in production as an unanchored sweep);</item>
///   <item>a source scan over the platform's AI-facing projects fails on any NEW file that builds a
///     <c>nodeType:Skill|Agent|LanguageModel|ModelProvider</c> query from a literal — the allow-list
///     names the seam itself and the files whose literals are NOT resolution (attribute metadata,
///     prompt text, per-partition credential lookup), each with the reason.</item>
/// </list>
/// The scan runs against the repo checkout the test assembly was built from; it is skipped when no
/// checkout is reachable (a packaged test run), never silently green.
/// </summary>
public class AiResolutionSeamAuditTest
{
    // Files where a resolution-shaped literal is LEGITIMATE — with why. Anything else that builds a
    // resolution query from a literal must go through AiSourceCatalog.
    private static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["src/MeshWeaver.AI/AiSourceCatalog.cs"] = "THE seam — defines the defaults.",
        ["src/MeshWeaver.AI/AgentPickerProjection.cs"] = "The canonical query BUILDERS the seam's defaults are derived from.",
        ["src/MeshWeaver.AI/AiSettingsNodeType.cs"] = "The default templates + resolvers that delegate to the seam.",
        ["src/MeshWeaver.AI/SkillNodeType.cs"] = "SkillQueries — the builder-backed fallback the seam degrades to.",
        ["src/MeshWeaver.AI/ThreadComposer.cs"] = "[MeshNode] attribute metadata for the composer's editor fields — static picker defaults; the live picker resolves through the seam (ObserveAgents/ObserveModels).",
        ["src/MeshWeaver.AI/AgentChatClient.cs"] = "Prompt text telling the model how to create a skill — not a resolution.",
        ["src/MeshWeaver.AI/Plugins/SkillTool.cs"] = "Tool descriptions/help text — not a resolution.",
        ["src/MeshWeaver.AI/MeshPlugin.cs"] = "Tool parameter description text — not a resolution.",
        ["src/MeshWeaver.AI/ChatClientCredentialResolver.cs"] = "Credential lookup by the MODEL's own partition set — a security seam keyed on the selected model, not a user-facing resolution.",
        ["src/MeshWeaver.AI/Stores/MeshAgentSkillsSource.cs"] = "Consumes AiSettingsNodeType.ObserveSkillQueries; its literal is documentation.",
        ["src/MeshWeaver.AI/Completion/SkillAutocompleteProvider.cs"] = "Consumes ObserveSkillQueries; literal is documentation.",
        ["src/MeshWeaver.AI/AiSourcesInstallHook.cs"] = "Registers package sources INTO the settings — the write side of the seam.",
        ["src/MeshWeaver.AI/BuiltInSkillProvider.cs"] = "Static-repo import source (writes the platform Skill partition) — not a resolution.",
        ["src/MeshWeaver.AI/BuiltInAgentProvider.cs"] = "Static-repo import source for the offline Agent partition — not a resolution.",
        ["src/MeshWeaver.AI/AgentNodeType.cs"] = "Type registration + comments naming the namespace — not a resolution.",
        ["src/MeshWeaver.AI/LanguageModelNodeType.cs"] = "Type registration — not a resolution.",
        ["src/MeshWeaver.AI/ModelProviderNodeType.cs"] = "Type registration — not a resolution.",
        ["src/MeshWeaver.AI/ModelTierNodeType.cs"] = "Type registration — not a resolution.",
        ["src/MeshWeaver.AI/Navigation/NavigationResolver.cs"] = "Consumes ObserveSkillQueries; any literal left is documentation.",
        ["src/MeshWeaver.Graph/NodeTypeLayoutAreas.cs"] = "Lists the agents a NODE TYPE's partition ships — a per-type listing anchored on that hub, not the viewer's resolution.",
        ["src/MeshWeaver.Cli/Program.cs"] = "CLI help text example.",
    };

    // The consumers that MUST go through the seam, and the seam symbol each must reference.
    private static readonly (string File, string Symbol)[] EntryPoints =
    {
        ("src/MeshWeaver.AI/AgentView.cs", "ObserveAgentQueries("),
        ("src/MeshWeaver.AI/Navigation/NavigationResolver.cs", "ObserveSkillQueries("),
        ("src/MeshWeaver.AI/AiCatalogLayoutAreas.cs", "AiSourceCatalog.Resolve("),
        ("src/MeshWeaver.AI/Stores/MeshAgentSkillsSource.cs", "ObserveSkillQueries("),
        ("src/MeshWeaver.AI/Completion/SkillAutocompleteProvider.cs", "SkillQueries("),
        ("src/MeshWeaver.AI/AgentPickerProjection.cs", "ObserveModelQueries("),
        ("src/MeshWeaver.Blazor.Portal/Chat/ThreadChatView.razor.cs", "ObserveModels(workspace, Hub, Hub.ServiceProvider"),
        ("src/MeshWeaver.AI/AiSettingsNodeType.cs", "AiSourceCatalog.Resolve("),
    };

    private static readonly Regex ResolutionLiteral = new(
        @"nodeType:(Skill|Agent|LanguageModel|ModelProvider)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ScannedRoots = { "src/MeshWeaver.AI", "src/MeshWeaver.Graph", "src/MeshWeaver.Blazor.Portal" };

    // The checkout root: the first ancestor of the test assembly that holds the platform's
    // src/MeshWeaver.AI project — works from a worktree, a clone, or bin/ of either.
    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "MeshWeaver.AI", "MeshWeaver.AI.csproj")))
                return dir.FullName;
        return null;
    }

    [Fact]
    public void EveryResolutionEntryPoint_GoesThroughTheSeam()
    {
        var root = RepoRoot();
        Assert.SkipWhen(root is null, "No repo checkout reachable from the test assembly — the audit needs the sources.");
        foreach (var (file, symbol) in EntryPoints)
        {
            var path = Path.Combine(root!, file);
            Assert.True(File.Exists(path), $"Entry point moved or vanished: {file} — update the audit.");
            var source = File.ReadAllText(path);
            Assert.True(source.Contains(symbol, StringComparison.Ordinal),
                $"{file} no longer resolves through the seam ({symbol} not referenced) — direct node access crept back.");
        }
    }

    [Fact]
    public void NoResolutionLiteral_OutsideTheAllowList()
    {
        var root = RepoRoot();
        Assert.SkipWhen(root is null, "No repo checkout reachable from the test assembly — the audit needs the sources.");
        var offenders = new List<string>();
        foreach (var scanned in ScannedRoots)
        {
            var dir = Path.Combine(root!, scanned);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                var relative = Path.GetRelativePath(root!, file).Replace('\\', '/');
                if (Allowed.ContainsKey(relative))
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimStart();
                    // Comments and XML docs may NAME a query; only code that builds one counts.
                    if (line.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    if (ResolutionLiteral.IsMatch(line))
                        offenders.Add($"{relative}:{i + 1}: {line.Trim()}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Resolution query literals outside the seam — route them through AiSourceCatalog "
            + "(or allow-list the file WITH a reason in AiResolutionSeamAuditTest):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void AllowList_NamesOnlyFilesThatExist_WithAReason()
    {
        var root = RepoRoot();
        Assert.SkipWhen(root is null, "No repo checkout reachable from the test assembly.");
        foreach (var (file, reason) in Allowed)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{file}: an allow-list entry needs its reason.");
            Assert.True(File.Exists(Path.Combine(root!, file)), $"Stale allow-list entry: {file} no longer exists.");
        }
    }
}
