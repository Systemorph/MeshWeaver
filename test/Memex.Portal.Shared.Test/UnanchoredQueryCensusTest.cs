using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The census of every <c>nodeType:</c> query core issues at RUNTIME that names no partition in its
/// text, fed to the decision the Postgres planner takes before running it (#3202, Plugins #1231):
/// <c>IsSufficientlySpecified(parsed) || ResolvesByRoutingHint(parsed)</c>, the latter against the
/// REAL <see cref="MeshConfiguration"/> of a running mesh. Everything else is refused at runtime
/// with an <c>UnanchoredQueryException</c> — a fault nothing compiles differently for and no test
/// used to catch, which is how the sign-in path broke for every user with both repositories green.
///
/// <para><b>The finding (2026-09-03, before this change).</b> Of the query literals under
/// <c>src/</c> and <c>memex/</c> with <c>nodeType:</c> and no <c>path:</c>/<c>namespace:</c>/
/// <c>partitions:all</c> on the same line, the ones the planner REFUSED — every other one was either
/// anchored on an adjacent line or pinned by a routing rule — are listed in
/// <see cref="RefusedBeforeThisChange"/>: the sign-in role fold, the home page's root and
/// "shared with me" legs, the sitemap, every NodeType-catalog enumeration (compile sweep, pre-warm,
/// prebuilt adoption, create menu, MCP catalog, cell surfaces), the UI-contribution catalog, the
/// outbound-mail watch, the event-subscription runner, the GitHub webhook's config scan, the
/// instance registry's id lookups, the What's New type lane, the Code usage prior, the stranded-
/// instance probe, the root subject picker and the plugin-catalog watcher. Each is now either
/// ANCHORED (the sign-in fold — three pinned homes) or DECLARED mesh-wide through
/// <see cref="MeshWideQuery"/> with the reason at the call site.</para>
///
/// <para><b>How this can fail.</b> The corpus is asserted non-empty and every entry must parse to
/// a query naming a node type (a matcher that matched nothing cannot pass); the known-refused
/// shapes are asserted REFUSED and the known-anchored ones ANCHORED before the corpus is judged (a
/// classifier that answered "served" to everything cannot pass); a parse or configuration fault
/// propagates — nothing here is caught; and <see cref="TheHarnessGoesRedWhenTheRulesAreTakenAway"/>
/// mutates the decision's input and asserts the verdict moves.</para>
/// </summary>
public class UnanchoredQueryCensusTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshConfiguration MeshConfig => Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

    /// <summary>
    /// Every runtime query core issues whose TEXT names no partition, as the code issues it today —
    /// built from the public builders where the site exposes one, otherwise a literal copy of the
    /// site's shape with representative substitutions and the site named. A site edited to a new
    /// shape without updating its row here is exactly the drift the census exists to notice.
    /// </summary>
    private static IEnumerable<(string Site, string Query)> Corpus()
    {
        // ── sign-in / identity ──────────────────────────────────────────────────────────────
        foreach (var leg in OnboardingMiddleware.RoleQueries("rbuergi"))
            yield return ("OnboardingMiddleware.LoadUserRoles", leg);
        yield return ("OnboardingMiddleware.FindUserByEmail", OnboardingMiddleware.UserByEmailQuery("a@b.c"));
        yield return ("UserIdentityCache.DirectoryQuery", UserIdentityCache.DirectoryQuery.Query!);
        yield return ("DevAuthController legacy User fallback", "nodeType:User");
        yield return ("GroupInviteExtensions / SpaceInviteService", "nodeType:User content.email:a@b.c");
        foreach (var q in AccessSubjectQueries.ForScope(null))
            yield return ("AccessSubjectQueries.ForScope(root)", q);
        foreach (var q in AccessSubjectQueries.ForScope("acme/doc"))
            yield return ("AccessSubjectQueries.ForScope(acme/doc)", q);
        foreach (var q in SecurityQueries.AllShapes)
            yield return ("SecurityQueries", q);

        // ── portal host (memex/Memex.Portal.Shared) ────────────────────────────────────────
        yield return ("OutboundEmailSender.WatchQuery", OutboundEmailSender.WatchQuery);
        foreach (var q in WhatsNewSettingsTab.ListingQueries)
            yield return ("WhatsNewSettingsTab.ListingQueries", q);
        yield return ("SeoEndpoints sitemap candidates", MeshWideQuery.Declare("nodeType:Space is:main limit:500"));
        yield return ("MeshWeaverInstanceService id lookup", MeshWideQuery.Declare("nodeType:MeshWeaverInstance id:inst-1"));

        // ── platform (src/) ────────────────────────────────────────────────────────────────
        yield return ("NodeType catalog enumerations (pre-warm, sweep, recompile, prebuilt, cells, MCP)", MeshWideQuery.OfType("NodeType"));
        yield return ("NodeTypeLayoutAreas compile sweep", MeshWideQuery.Declare("nodeType:NodeType select:path,id,name,nodeType,content"));
        yield return ("CompletionUsageIndex", MeshWideQuery.Declare("nodeType:Code limit:2000"));
        yield return ("UiContributionCatalog", MeshWideQuery.OfType("UiContribution"));
        yield return ("CreateLayoutArea namespace picker", MeshWideQuery.OfType("Space"));
        yield return ("EventSubscriptionRunner", MeshWideQuery.OfType("Invitation") + " content.email:a@b.c");
        yield return ("GitHubWebhookProcessor / ModuleDiscovery / InstanceComboReader", MeshWideQuery.OfType("GitHubSync"));
        yield return ("NodeTypeInstanceProbe", MeshWideQuery.OfType("Acme/Story"));
        yield return ("PluginUpdateWatcher", MeshWideQuery.OfType("PluginCatalog"));
        yield return ("InstanceGrantAdminSettingsTab / InstancePlanService", MeshWideQuery.Declare("nodeType:MeshWeaverInstance id:inst-1"));
        yield return ("UserActivityLayoutAreas shared-with-me", MeshWideQuery.Declare("nodeType:AccessAssignment content.accessObject:rbuergi"));
        yield return ("UserActivityLayoutAreas home root leg", "namespace: is:main is:content nodeType:Space sort:Name-asc partitions:all");
        yield return ("UserActivityLayoutAreas home subtree scope", "is:main is:content -nodeType:User sort:Name-asc partitions:all");
    }

    /// <summary>
    /// 🚨 THE FINDING — the shapes the planner refused before this change, one per call site, with
    /// representative substitutions. Kept as the positive control: a classifier that cannot see a
    /// refusal in THESE cannot be trusted to see one in the corpus.
    /// </summary>
    private static readonly (string Site, string Query)[] RefusedBeforeThisChange =
    [
        ("OnboardingMiddleware.LoadUserRoles (the 503 — #3202)", "nodeType:AccessAssignment content.accessObject:\"rbuergi\" scope:subtree limit:all"),
        ("UserActivityLayoutAreas.ObserveSharedTargets (home: shared with me)", "nodeType:AccessAssignment content.accessObject:rbuergi"),
        ("UserActivityLayoutAreas.FirstLevelUnion root leg (home: spaces)", "namespace: is:main is:content nodeType:Space sort:Name-asc"),
        ("UserActivityLayoutAreas.CatalogQuery subtree scope (home: everything)", "is:main is:content -nodeType:User sort:Name-asc"),
        ("SeoEndpoints sitemap candidates", "nodeType:Space is:main limit:500"),
        ("MeshWeaverInstanceService.AdoptKeyHash / IsIdAvailable", "nodeType:MeshWeaverInstance id:inst-1"),
        ("InstancePlanService / InstanceGrantAdminSettingsTab", "nodeType:MeshWeaverInstance"),
        ("InstanceGrantAdminSettingsTab keys", "nodeType:RegistrationKey"),
        ("OutboundEmailSender.WatchQuery", "nodeType:Email content.direction:Outbound -content.status:Sending -content.status:Sent -content.status:Failed"),
        ("WhatsNewSettingsTab type lane", "nodeType:WhatsNew"),
        ("DynamicTypePreWarmer / NodeTypeRecompileExtensions / ShippedPrebuiltBundles / CellSurfaceAssemblyProvider / CreatableTypesProvider / MeshOperations.Catalog", "nodeType:NodeType"),
        ("NodeTypeLayoutAreas compile sweep", "nodeType:NodeType select:path,id,name,nodeType,content"),
        ("CompletionUsageIndex", "nodeType:Code limit:2000"),
        ("UiContributionCatalog", "nodeType:UiContribution"),
        ("CreateLayoutArea namespace picker", "nodeType:Space"),
        ("EventSubscriptionRunner (a trigger type with no routing rule)", "nodeType:Acme/Order content.status:Open"),
        ("GitHubWebhookProcessor / ModuleDiscoveryService / InstanceComboReader", "nodeType:GitHubSync"),
        ("InstanceComboReader discovery records", "nodeType:ModuleDiscovery"),
        ("NodeTypeInstanceProbe", "nodeType:Acme/Story"),
        ("PluginUpdateWatcher", "nodeType:PluginCatalog"),
        ("AccessSubjectQueries.Groups(root)", "nodeType:Group"),
    ];

    /// <summary>
    /// Shapes that were NOT refused although their text names no partition — the ones a routing
    /// rule pins. Listed so the census records WHY they are absent from the finding, and so the
    /// rule's disappearance would show up here.
    /// </summary>
    private static readonly (string Site, string Query, string Partition)[] PinnedByRule =
    [
        ("OnboardingMiddleware.FindUserByEmail", "nodeType:User content.email:a@b.c limit:1", "Auth"),
        ("UserIdentityCache.DirectoryQuery", "nodeType:User", "Auth"),
        ("EventSubscriptionRunner (Invitation trigger)", "nodeType:Invitation", "Admin"),
        ("EventSubscriptionRunner (EventSubscription trigger)", "nodeType:EventSubscription", "Admin"),
    ];

    [Fact]
    public void ThePlannerRefusedEveryShapeInTheFinding_AndPinsTheRuledOnes()
    {
        var configuration = MeshConfig;
        RefusedBeforeThisChange.Should().NotBeEmpty();
        foreach (var (site, query) in RefusedBeforeThisChange)
            QueryRouteClassifier.VerdictOf(query, configuration).Should().Be(PlannerVerdict.Refused,
                $"{site} issued '{query}', which names no partition and is pinned by no rule");

        PinnedByRule.Should().NotBeEmpty();
        foreach (var (site, query, partition) in PinnedByRule)
        {
            QueryRouteClassifier.VerdictOf(query, configuration).Should().Be(PlannerVerdict.PinnedByRoutingRule,
                $"{site} issues '{query}', which a registered routing rule pins");
            configuration.ResolveRoutingHints(new QueryParser().Parse(query)).Partition.Should().Be(partition);
        }
    }

    /// <summary>
    /// The census proper: after this change no runtime query core issues is refused. The refused
    /// set is printed whatever its size — an empty one is the assertion, never a silent pass.
    /// </summary>
    [Fact]
    public void NoRuntimeQueryCoreIssuesIsRefused()
    {
        var configuration = MeshConfig;
        var corpus = Corpus().ToArray();
        corpus.Should().NotBeEmpty("a census over nothing proves nothing");

        var parser = new QueryParser();
        foreach (var (site, query) in corpus)
        {
            var parsed = parser.Parse(query);
            (parsed.ExtractNodeType() is not null || parsed.IsMain == true || !string.IsNullOrEmpty(parsed.Path))
                .Should().BeTrue(
                    $"{site}: '{query}' must parse to a query the planner can classify — a row the parser "
                    + "reads as empty is a row the census is not measuring");
        }

        var verdicts = corpus
            .Select(x => (x.Site, x.Query, Verdict: QueryRouteClassifier.VerdictOf(x.Query, configuration)))
            .ToArray();
        var refused = verdicts.Where(v => v.Verdict == PlannerVerdict.Refused).ToArray();

        Output.WriteLine($"census: {verdicts.Length} queries, {refused.Length} refused");
        foreach (var (site, query, verdict) in verdicts)
            Output.WriteLine($"  [{verdict}] {site}: {query}");

        refused.Should().BeEmpty(
            "every runtime query core issues must be anchored, pinned by a routing rule, or DECLARED "
            + "mesh-wide through MeshWideQuery with the reason at the call site — the storage layer "
            + "refuses anything else at runtime, and that refusal is invisible to the compiler (#3202). "
            + "Refused: " + string.Join("; ", refused.Select(r => $"{r.Site}: '{r.Query}'")));

        // The declared fan-outs are the ones whose cost is knowingly paid — record how many.
        verdicts.Count(v => v.Verdict == PlannerVerdict.DeclaredFanOut).Should().BePositive(
            "core has genuine catalogs (NodeType, UiContribution, Space roots) that ARE mesh-wide; a census "
            + "that finds none of them declared is reading the wrong corpus");
    }

    /// <summary>
    /// 🚨 The mutation: the decision's second input is the routing rules. Take them away and the
    /// rule-pinned rows must flip to REFUSED, so a corpus that passes only because of a rule cannot
    /// pass under a planner without it. If this stayed green, the harness would be measuring the
    /// classifier's optimism rather than the planner's decision.
    /// </summary>
    [Fact]
    public void TheHarnessGoesRedWhenTheRulesAreTakenAway()
    {
        var withRules = Corpus().Select(x => QueryRouteClassifier.VerdictOf(x.Query, MeshConfig)).ToArray();
        var withoutRules = Corpus().Select(x => QueryRouteClassifier.VerdictOf(x.Query, configuration: null)).ToArray();

        withRules.Should().NotContain(PlannerVerdict.Refused);
        withoutRules.Should().Contain(PlannerVerdict.Refused,
            "the corpus carries rule-pinned reads (nodeType:User), so removing the rules must produce a refusal");
        withoutRules.Count(v => v == PlannerVerdict.Refused).Should().Be(
            withRules.Count(v => v == PlannerVerdict.PinnedByRoutingRule),
            "exactly the rule-pinned rows flip; anchored and declared rows do not depend on rules");
    }
}
