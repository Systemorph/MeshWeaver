using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Ratchet for policy <c>platform-backwards-compatibility</c> (the LADDER,
/// <c>Doc/Architecture/ContinuousDeliveryContract</c> → "The ladder"): platform builds are
/// backwards compatible within a compatibility epoch, so the PLATFORM set is delivered, announced
/// and judged on its own, and the MeshWeaver.Plugins re-seal for the new build is an independent
/// follow-up that can never hold, fail or re-publish it.
///
/// <para>What it holds, in <c>.github/workflows/main-cd.yml</c>: no platform delivery job
/// (<c>promote</c>, <c>verify-images</c>, <c>publish-bake</c>, <c>notify-platform-update</c>,
/// <c>delivery-verdict</c>) reaches a <c>plugins-*</c> or <c>satellite-compat*</c> job through its
/// <c>needs</c>, TRANSITIVELY; <c>delivery-verdict</c> reads no result of one; the <c>handoff</c>
/// step's <c>DELIVERY_LEGS</c> names none; the <c>ci-failure</c> alert keys on a DIRECT need failing
/// (never <c>failure()</c>, which is true when any ANCESTOR failed — and the compatibility legs have
/// <c>published-modules</c> as an ancestor); and — policy <c>platform-module-deploy-separate</c> —
/// the workflow carries NO job that packs, bakes or seals a MeshWeaver.Plugins module at all (no
/// <c>plugins-*</c> job): modules publish on their own lanes, and core only READS the published set
/// for its compatibility evidence.</para>
///
/// <para><c>arm</c> writes the fleet's arming tags only for a promoted set whose PLATFORM verdict is
/// green (its ladder, and control running it — policies <c>platform-deploy-control-first</c> and
/// <c>platform-module-deploy-separate</c>; no MeshWeaver.Plugins verdict is read), and
/// <c>notify-platform-update</c> follows <c>arm</c>. Neither is a need of <c>delivery-verdict</c>
/// (<see cref="TheArmingIsNeverAPlatformDeliveryLeg"/>), so the platform set is still promoted,
/// verified, baked and judged on its own.</para>
///
/// <para>Measured before the split: CD 9309/9311/9313/9315/9316/9317/9320 each went red on
/// <c>Plugins: pack … / Module tests</c> — a MeshWeaver.Plugins unit test — over platform sets that
/// were promoted, verified and baked; the red handed HEAD on to a full re-publish and filed
/// <c>ci-failure</c> ("no deployable image was published"). Every detector below has a negative
/// control that re-introduces that coupling and must be caught.</para>
/// </summary>
public class PlatformDeliveryNeverWaitsOnPluginsGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";

    /// <summary>The platform delivery jobs — the set that must never wait on a Plugins job.</summary>
    private static readonly ImmutableArray<string> PlatformJobs =
        ["promote", "verify-images", "publish-bake", "notify-platform-update", "delivery-verdict"];

    /// <summary>The compatibility ladder — the half of the PLATFORM verdict that reads modules.</summary>
    private const string Ladder = "platform-ladder-compat";


    [Fact]
    public void ThePlatformDelivery_NeverWaitsOn_NorFailsFor_APluginsJob()
    {
        var problems = Problems(File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow)));
        Assert.True(problems.Count == 0,
            "the PLATFORM delivery in main-cd.yml is coupled to a Plugins job again (policy "
            + "platform-backwards-compatibility — a platform roll never waits for a plugin seal):\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>Negative controls: each re-introduces one coupling shape and must be caught.</summary>
    [Fact]
    public void TheDetector_CatchesEveryCouplingShape()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow));
        Assert.Empty(Problems(text));

        // 1. a Plugins job as a DIRECT need of the verdict.
        Assert.Contains(Problems(Mutate(text,
                "            publish-bake, verify-images]",
                "            publish-bake, verify-images, plugins-bake]")),
            p => p.Contains("delivery-verdict", StringComparison.Ordinal) && p.Contains("plugins-bake", StringComparison.Ordinal));

        // 2. a module job reached TRANSITIVELY — through a need of a need (publish-bake → published-modules).
        Assert.Contains(Problems(Mutate(text,
                "    needs: [gate, plugin-test-image, portal-image, promote]",
                "    needs: [gate, plugin-test-image, portal-image, promote, published-modules]")),
            p => p.Contains("publish-bake", StringComparison.Ordinal) && p.Contains("published-modules", StringComparison.Ordinal));

        // 3. a Plugins leg back in the handoff's DELIVERY_LEGS.
        Assert.Contains(Problems(Mutate(text,
                "            ${{ needs.publish-bake.result }}\n          # 🚨 The one-hop bound.",
                "            ${{ needs.publish-bake.result }} ${{ needs.plugins-bake.result }}\n          # 🚨 The one-hop bound.")),
            p => p.Contains("DELIVERY_LEGS", StringComparison.Ordinal));

        // 4. the ci-failure alert back on `failure()` (ancestor semantics).
        Assert.Contains(Problems(Mutate(text,
                "    if: always() && contains(needs.*.result, 'failure')",
                "    if: failure()")),
            p => p.Contains("alert-on-failure", StringComparison.Ordinal));

        // 6. the ladder (the arming chain's one sanctioned module read) reaching anything but the
        //    published set — e.g. a satellite leg — is caught even though `notify` stops at it.
        Assert.Contains(Problems(Mutate(text,
                "    needs: [preflight, gate, promote, compat-images, published-modules]",
                "    needs: [preflight, gate, promote, compat-images, published-modules, satellite-compat]")),
            p => p.Contains("platform-ladder-compat", StringComparison.Ordinal) && p.Contains("satellite-compat", StringComparison.Ordinal));

        // 5. a Plugins packing / sealing job back in the PLATFORM deploy (policy
        //    platform-module-deploy-separate) — even one no platform job needs.
        Assert.Contains(Problems(Mutate(text, "\n  published-modules:\n", "\n  plugins-modules:\n")),
            p => p.Contains("plugins-modules", StringComparison.Ordinal) && p.Contains("platform-module-deploy-separate", StringComparison.Ordinal));
    }

    /// <summary>
    /// The promotion gate reads a Plugins verdict — so it must stay OUT of the platform delivery
    /// verdict, directly and through its only dependant, or a Plugins red would once again turn a
    /// platform delivery red (the CD 9309…9320 shape, one level up).
    /// </summary>
    [Fact]
    public void TheArmingIsNeverAPlatformDeliveryLeg()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow));
        var start = text.IndexOf("\n  delivery-verdict:", StringComparison.Ordinal);
        Assert.True(start >= 0, "delivery-verdict is gone from main-cd.yml");
        var m = Regex.Match(text[start..], @"\n    needs:\s*\[(?<n>[^\]]*)\]");
        Assert.True(m.Success, "delivery-verdict has no `needs: [...]` list");
        var needs = m.Groups["n"].Value.Split(',').Select(n => n.Trim()).ToArray();
        Assert.Contains("promote", needs);
        Assert.DoesNotContain("arm", needs);
        Assert.DoesNotContain("notify-platform-update", needs);
    }

    /// <summary>
    /// 🚨 Ratchet (c): never pin an image tag — not in a chart value, not in a compose file, not in
    /// a <c>Hosting/Deployment</c> record this repository ships. The platform image an install runs
    /// is chosen by its update policy at roll time; a pinned value freezes it, and every past
    /// compatibility break "fixed" by a pin became the next outage. Test fixtures that model a
    /// live cluster's state (<c>**/test/fixtures/**</c>, <c>test/**</c>) are data about the world,
    /// not configuration, and are not in scope.
    /// </summary>
    [Fact]
    public void NoShippedChartOrRecord_PinsAPlatformImage()
    {
        var root = FindRepoRoot();
        var problems = ShippedConfigFiles(root)
            .SelectMany(f => PinProblems(Path.GetRelativePath(root, f), File.ReadAllText(f)))
            .ToList();
        Assert.True(problems.Count == 0,
            "a shipped chart value, compose file or Deployment record PINS a platform image tag — "
            + "never pin (the update policy chooses the image at roll time):\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void ThePinDetector_FiresOnEachPinShape_AndIsSilentOnFloatingDefaults()
    {
        Assert.Empty(PinProblems("v.yaml", "portal:\n  image: \"ghcr.io/systemorph/memex-portal-ai:latest\"\n"));
        Assert.Empty(PinProblems("v.yaml", "image:\n  portal: \"memex-portal-ai\"\n  tag: \"latest\"\n"));
        Assert.Empty(PinProblems("v.yaml", "hostingOperator:\n  image: \"\"\n"));
        Assert.Empty(PinProblems("r.json", "{ \"pinnedImageTag\": null }"));
        Assert.Empty(PinProblems("v.yaml", "env:\n  Mcp__BaseUrl: \"http://memex-portal-service:8080\"\n"));
        Assert.Empty(PinProblems("v.yaml", "env:\n  Mcp__BaseUrl: \"http://memex-portal:8080\"\n"));

        Assert.NotEmpty(PinProblems("v.yaml", "portal:\n  image: \"ghcr.io/systemorph/memex-portal-ai:3.0.0-ci.9321\"\n"));
        Assert.NotEmpty(PinProblems("v.yaml", "migration:\n  image: \"cr.meshweaver.cloud/memex-migration@sha256:abc\"\n"));
        Assert.NotEmpty(PinProblems("v.yaml", "image:\n  portal: \"memex-portal-ai\"\n  tag: \"3.0.0-ci.9321\"\n"));
        Assert.NotEmpty(PinProblems("r.json", "{ \"pinnedImageTag\": \"3.0.0-ci.8080\" }"));
        Assert.NotEmpty(PinProblems("r.yaml", "content:\n  pinnedImageTag: 3.0.0-ci.8080\n"));
    }

    // ─────────────────────────────── detectors ───────────────────────────────

    /// <summary>Every coupling of the platform delivery to a Plugins job in <paramref name="workflowText"/>.</summary>
    internal static List<string> Problems(string workflowText)
    {
        var problems = new List<string>();
        var jobs = Jobs(workflowText);
        var needs = jobs.ToDictionary(kv => kv.Key, kv => NeedsOf(kv.Value));

        foreach (var job in PlatformJobs)
        {
            if (!jobs.ContainsKey(job))
            {
                problems.Add($"platform job `{job}` is missing — the guard's subject moved; re-point it rather than let it pass on nothing");
                continue;
            }
            // The ONE sanctioned reach (policy `platform-module-deploy-separate`): the release event
            // follows `arm`, and `arm` needs the compatibility LADDER — part of the PLATFORM verdict —
            // which reads the PUBLISHED module set. So for the arming chain the ladder is a boundary:
            // what it needs is judged on its own below, and nothing else module-side may be reached.
            var boundary = job == "notify-platform-update" ? Ladder : null;
            foreach (var reached in Closure(job, needs, boundary).Where(IsPluginsSide).OrderBy(x => x, StringComparer.Ordinal))
                problems.Add($"`{job}` reaches `{reached}` through its needs (transitively)");
        }
        if (needs.TryGetValue(Ladder, out var ladderNeeds))
            foreach (var reached in Closure(Ladder, needs).Where(j => IsPluginsSide(j) && j != "published-modules").OrderBy(x => x, StringComparer.Ordinal))
                problems.Add($"`{Ladder}` reaches `{reached}` — the ladder may read only the PUBLISHED module set (`published-modules`)");
        else
            problems.Add($"`{Ladder}` is missing — the platform verdict has no compatibility half; re-point the guard rather than let it pass on nothing");

        if (jobs.TryGetValue("delivery-verdict", out var verdict))
        {
            var body = Serialize(verdict);
            foreach (Match m in Regex.Matches(body, @"needs\.((?:plugins|satellite-compat)[A-Za-z0-9_-]*)\."))
                problems.Add($"`delivery-verdict` reads `needs.{m.Groups[1].Value}` — a Plugins result must not decide the platform verdict");
            var legs = Regex.Match(body, @"DELIVERY_LEGS:\s*(?<v>[^\n]*(?:\n\s+\$\{\{[^\n]*)*)");
            if (!legs.Success)
                problems.Add("`delivery-verdict` has no `DELIVERY_LEGS` for the handoff — the guard's subject moved");
            else if (Regex.IsMatch(legs.Groups["v"].Value, @"needs\.(plugins|satellite-compat)"))
                problems.Add("the handoff's `DELIVERY_LEGS` names a Plugins leg — a Plugins red would hand HEAD on to a full platform re-publish");
        }

        if (jobs.TryGetValue("alert-on-failure", out var alert))
        {
            var cond = ScalarOf(alert, "if") ?? "";
            if (!cond.Contains("contains(needs.*.result, 'failure')", StringComparison.Ordinal)
                || Regex.IsMatch(cond, @"(^|[^.\w])failure\(\)"))
                problems.Add("`alert-on-failure` must key on a DIRECT need failing (`always() && contains(needs.*.result, 'failure')`), never `failure()` — "
                             + "that is true when any ANCESTOR failed, and the compatibility legs have `published-modules` as an ancestor");
        }
        else
            problems.Add("`alert-on-failure` is missing — the guard's subject moved");

        // 🚨 Policy `platform-module-deploy-separate`: the platform deploy packs, bakes and seals NO
        // module. A `plugins-*` job is that coupling by name, whether or not a platform job needs it.
        foreach (var job in jobs.Keys.Where(j => j.StartsWith("plugins-", StringComparison.Ordinal)
                                                 || j.StartsWith("report-plugins", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
            problems.Add($"`{job}` is a MeshWeaver.Plugins packing/sealing job inside the platform deploy (policy platform-module-deploy-separate) — modules publish on their own lanes");
        return problems;
    }

    private static bool IsPluginsSide(string job) =>
        job.StartsWith("plugins-", StringComparison.Ordinal)
        || job.StartsWith("satellite-compat", StringComparison.Ordinal)
        || job == "published-modules";

    private static HashSet<string> Closure(string job, IReadOnlyDictionary<string, List<string>> needs, string? boundary = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(needs.TryGetValue(job, out var n) ? n : []);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next)) continue;
            if (next == boundary) continue;
            if (needs.TryGetValue(next, out var more))
                foreach (var m in more) stack.Push(m);
        }
        return seen;
    }

    /// <summary>Pin findings in one shipped file: a platform image carrying a version or digest,
    /// an <c>image.tag</c> beside <c>image.portal</c> that is not <c>latest</c>, or a non-empty
    /// <c>pinnedImageTag</c>.</summary>
    internal static List<string> PinProblems(string relativePath, string text)
    {
        var problems = new List<string>();
        // An image REFERENCE: the repository name as the last path segment, then a tag and/or a
        // digest. `memex-portal:8080` (a host and port in a URL) is not one — a port is all digits.
        foreach (Match m in Regex.Matches(text,
                     @"memex-(?:portal-ai|portal|migration)(?![A-Za-z0-9-])(?<ref>(?::[A-Za-z0-9._-]+)?(?:@sha256:[0-9a-f]+)?)"))
        {
            var reference = m.Groups["ref"].Value;
            if (reference.Length == 0 || reference == ":latest") continue;
            if (Regex.IsMatch(reference, @"^:\d+$")) continue;
            problems.Add($"{relativePath}: `{m.Value}` pins a platform image");
        }
        var imageBlock = Regex.Match(text, @"(?m)^(?<indent>\s*)image:\s*\n(?<body>(?:\k<indent>\s+.*\n?)+)");
        if (imageBlock.Success && Regex.IsMatch(imageBlock.Groups["body"].Value, @"(?m)^\s*portal:"))
        {
            var tag = Regex.Match(imageBlock.Groups["body"].Value, @"(?m)^\s*tag:\s*""?(?<t>[^""\s#]*)");
            if (tag.Success && tag.Groups["t"].Value is { Length: > 0 } t && t != "latest")
                problems.Add($"{relativePath}: `image.tag: {t}` pins the platform image");
        }
        foreach (Match m in Regex.Matches(text, @"""?pinnedImageTag""?\s*:\s*(?<v>""[^""]*""|[^\s,}]+)"))
        {
            var v = m.Groups["v"].Value.Trim('"');
            if (v.Length == 0 || v == "null" || v == "~") continue;
            problems.Add($"{relativePath}: `pinnedImageTag: {v}` pins a Deployment record");
        }
        return problems;
    }

    private static IEnumerable<string> ShippedConfigFiles(string root)
    {
        var deploy = Path.Combine(root, "deploy");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(deploy, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal)
                        || f.EndsWith(".json", StringComparison.Ordinal))
            // Only the operator's TEST FIXTURES are out of scope — recorded cluster state, not
            // configuration. Chart TEMPLATES are shipped configuration and are in scope: a
            // hard-coded tag there would pin every install the chart renders.
            .Where(f => !f.Contains($"{sep}test{sep}fixtures{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}node_modules{sep}", StringComparison.Ordinal));
    }

    // ─────────────────────────────── YAML plumbing ───────────────────────────────

    private static Dictionary<string, YamlMappingNode> Jobs(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
        return jobs.Children.ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => (YamlMappingNode)kv.Value, StringComparer.Ordinal);
    }

    private static List<string> NeedsOf(YamlMappingNode job)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("needs"), out var n)) return [];
        return n switch
        {
            YamlScalarNode s => [s.Value!],
            YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value!).ToList(),
            _ => [],
        };
    }

    private static string? ScalarOf(YamlMappingNode job, string key) =>
        job.Children.TryGetValue(new YamlScalarNode(key), out var v) ? (v as YamlScalarNode)?.Value : null;

    private static string Serialize(YamlNode node)
    {
        var doc = new YamlStream(new YamlDocument(node));
        using var w = new StringWriter();
        doc.Save(w, assignAnchors: false);
        return w.ToString();
    }

    private static string Mutate(string text, string from, string to)
    {
        Assert.True(text.Contains(from, StringComparison.Ordinal),
            $"negative control cannot apply — the anchor moved, so the control would pass having mutated nothing: `{from}`");
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
