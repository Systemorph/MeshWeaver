using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🔴 <b>ONE query language, TWO providers, and the test that makes them answer alike —
/// Systemorph/MeshWeaver#3511, #3472.</b>
///
/// <para>The defect was never "the sweep is broken". It was that
/// <see cref="QueryEvaluator"/> (core: every in-memory, FileSystem and static-node host) and
/// <c>PostgreSqlSqlGenerator.MapSelector</c> (MeshWeaver.Plugins: every portal) disagreed about
/// WHICH SELECTORS EXIST, and nothing compared them — so the disagreement was found by reading
/// code, months late, and only because the instrument AGENTS.md prescribes for gating a production
/// deploy sat on top of it.</para>
///
/// <para><b>What it cost, measured.</b> On a live Postgres mesh, 2026-09-07:
/// <c>nodeType:NodeType compilationStatus:Error</c> returned 5,
/// <c>nodeType:NodeType compilationStatus:Ok</c> returned 195, and the two sets were disjoint — the
/// bare selector DISCRIMINATED. The same query in this evaluator matched nothing, because
/// <c>MeshNode</c> has no <c>compilationStatus</c> property and there was no content fallback: a
/// mesh full of broken types and a clean mesh both answered <c>count: 0</c>. "It never ran" and
/// "it passed" painted the same colour, which is the exact shape AGENTS.md forbids in a CI gate.</para>
///
/// <para><b>The fix is the fallback, not a special case.</b> <see cref="QueryEvaluator"/> now
/// resolves a selector the way SQL always did — the node's own field first, else the content field
/// of the same name — so <c>compilationStatus</c> is reachable because the RULE reaches it, not
/// because anybody named it. <see cref="SelectorResolutionCorpus"/> is the shared statement of that
/// rule; MeshWeaver.Plugins pins <c>MapSelector</c> against the same rows.</para>
/// </summary>
public class SweepSelectorReachesTheCompileStatusTest
{
    private const string FromContent = "SENTINEL-FROM-CONTENT";

    private static MeshNode BrokenType() =>
        new("Offer", "Crm")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Error,
                CompilationError = "CS0103: the name 'x' does not exist in the current context",
            },
        };

    private static MeshNode HealthyType() =>
        new("Client", "Crm")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
            },
        };

    // ────────────────────────────── the sweep's own selector ──────────────────────────────

    // 🚨 Every assertion below stringifies through INTERPOLATION, never `?.ToString()`.
    // `GetPropertyValue(...)?.ToString().Should().Be(...)` short-circuits the WHOLE chain when the
    // value is null — `.Should()` is never reached and the test passes having asserted nothing,
    // which is exactly the "verification step that cannot fail" this file exists to prevent. It
    // shipped that way in #3515 and was caught by running these tests against the UN-fixed
    // evaluator: both the `Error` and the `Ok` assertion stayed GREEN with the fallback removed.
    // Interpolation renders null as "", so a missing value fails loudly.

    [Fact]
    public void TheBareSelectorReachesTheCompileStatus()
        => $"{new QueryEvaluator().GetPropertyValue(BrokenType(), "compilationStatus")}"
            .Should().Be("Error",
                "🚨 THE FIX (#3511). This returned null for two releases: MeshNode has no such "
                + "property and the evaluator had no content fallback, so the comparison was "
                + "null == 'Error' — false for every node, and a mesh full of broken types "
                + "answered count: 0. Postgres has always read an unknown selector out of the "
                + "content JSONB, so the two providers gave different answers to one query; the "
                + "fallback is what makes them agree");

    [Fact]
    public void TheDottedSelectorReachesTheCompileStatus()
        => $"{new QueryEvaluator().GetPropertyValue(BrokenType(), "content.compilationStatus")}"
            .Should().Be("Error",
                "the dotted form worked before the fallback and must keep working: the evaluator "
                + "walks the selector part by part, so 'content' resolves MeshNode.Content and "
                + "'compilationStatus' then resolves NodeTypeDefinition.CompilationStatus "
                + "case-insensitively. It is the form AGENTS.md now prescribes, because it needs "
                + "no provider to have been fixed — it already reaches the field on a portal "
                + "running any image");

    [Fact]
    public void AHealthyTypeIsNotReportedBroken()
        => $"{new QueryEvaluator().GetPropertyValue(HealthyType(), "compilationStatus")}"
            .Should().Be("Ok",
                "THE CONTROL — a fallback that had stopped resolving anything, or one that "
                + "answered 'Error' for everything, would satisfy the first assertion and score "
                + "identically while making the sweep useless in the other direction");

    // ────────────────────── the ordering decision, and its falsification ──────────────────────

    [Fact]
    public void ANodeFieldBEATSASameNamedContentField()
    {
        var node = new MeshNode("Offer", "Crm")
        {
            Name = "the node's own name",
            Content = UntypedContent(("name", FromContent)),
        };

        new QueryEvaluator().GetPropertyValue(node, "name")
            .Should().Be("the node's own name",
                "🚨 THE ORDERING DECISION. The node's own field is consulted FIRST and content is "
                + "only the fallback — which is MapSelector's order (PropertyMap before the "
                + "n.content->> default) and the only order that changes nothing that already "
                + "works. Content-first would silently re-point every live query whose selector "
                + "names both, and name/description/category/icon/order/state/version are common "
                + "content field names, so name:Foo would start filtering on the content's name "
                + "for a large part of the mesh. This way round the ONLY selectors whose "
                + "resolution moves are the ones that resolved to null before, i.e. the ones that "
                + "matched nothing");
    }

    [Fact]
    public void AKnownFieldHOLDINGNullDoesNotFallIntoContent()
    {
        var node = new MeshNode("Offer", "Crm")
        {
            Description = null,
            Content = UntypedContent(("description", FromContent)),
        };

        new QueryEvaluator().GetPropertyValue(node, "description")
            .Should().BeNull(
                "🚨 the fallback keys on the property being ABSENT, never on its value being null. "
                + "Postgres answers the n.description COLUMN for this selector and never falls "
                + "through to the JSONB, so a node with no description must answer null here too. "
                + "Keying on nullness instead would have made every empty node field silently "
                + "start reading its content — a far bigger behaviour change than the one #3511 "
                + "asked for, and one that diverges from SQL in the opposite direction");
    }

    [Fact]
    public void TheNegatedUnknownSelectorNoLongerMatchesEverything()
    {
        var parsed = new QueryParser().Parse("-compilationStatus:Error");
        var evaluator = new QueryEvaluator();

        evaluator.Matches(BrokenType(), parsed).Should().BeFalse(
            "🚨 THE OTHER HALF OF THE BEHAVIOUR CHANGE, and the one that changes a RESULT SET "
            + "rather than just a lookup. CompareEqual(null, 'Error') was false, so NotEqual was "
            + "true: before the fallback, '-compilationStatus:Error' matched EVERY node including "
            + "the broken ones — a filter meant to exclude them returned them. It now excludes "
            + "this one, which is what Postgres already did");

        evaluator.Matches(HealthyType(), parsed).Should().BeTrue(
            "THE CONTROL for the negation — a fallback that made the predicate false for "
            + "everything would satisfy the assertion above and be just as wrong");
    }

    // ───────────────────────── the corpus: one rule, both providers ─────────────────────────

    /// <summary>
    /// Every selector the corpus records as reaching CONTENT must actually read the content here.
    /// The Plugins-side twin asserts <c>MapSelector</c> emits the same rows'
    /// <see cref="SelectorCase.SqlExpression"/>, so one list governs both providers.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContentSideCases))]
    public void AContentSideSelectorReadsTheContent(string selector)
    {
        var probe = SelectorResolutionCorpus.Cases.Single(c => c.Selector == selector);

        new QueryEvaluator()
            .GetPropertyValue(NodeWithContentAt(probe.Selector), probe.Selector)
            .Should().Be(FromContent,
                $"the corpus records '{probe.Selector}' as a content field on this provider "
                + $"({probe.Note}). Postgres reads it as {probe.SqlExpression}");
    }

    /// <summary>
    /// Every selector the corpus records as a NODE field must ignore a content field of the same
    /// name — the ordering claim, asserted per selector rather than once, and type-agnostically so
    /// it covers <c>order</c>, <c>version</c>, <c>state</c> and <c>lastModified</c> too.
    /// </summary>
    [Theory]
    [MemberData(nameof(NodeSideCases))]
    public void ANodeSideSelectorIgnoresASameNamedContentField(string selector)
    {
        var probe = SelectorResolutionCorpus.Cases.Single(c => c.Selector == selector);

        new QueryEvaluator()
            .GetPropertyValue(NodeWithContentAt(probe.Selector), probe.Selector)
            .Should().NotBe(FromContent,
                $"the corpus records '{probe.Selector}' as a node field on this provider, so a "
                + "content field of the same name must not shadow it. If this fails the fallback "
                + "has been reordered content-first, which silently re-points live queries");
    }

    [Fact]
    public void TheProvidersDisagreeOnExactlyTheRecordedSelectors()
        => string.Join(", ", SelectorResolutionCorpus.KnownDivergences.Select(c => c.Selector))
            .Should().Be(
                "createdBy, createdDate, lastModifiedBy, desiredId, syncBehavior, "
                + "excludeFromContext, isDefinitionOnly, isSatelliteType, preRenderedHtml, "
                + "hasExplicitMainNode",
                "🚨 #3511 does NOT close every divergence, and the residue is pinned so it cannot "
                + "grow in silence. Each of these is a MeshNode property that MapSelector's "
                + "PropertyMap does not list, so Postgres reads the content field of the same name "
                + "— empty for essentially every node — while this evaluator reads the property. "
                + "The first six have a REAL mesh_nodes column behind them (created_by, "
                + "created_date, last_modified_by, desired_id, sync_behavior, "
                + "exclude_from_context), so those are a Plugins fix: widen PropertyMap. The last "
                + "four exist only on MeshNode and cannot be reconciled that way. Adding a name "
                + "here is a claim that a provider changed — do not do it to make a test pass");

    public static TheoryData<string> ContentSideCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in SelectorResolutionCorpus.Cases.Where(c => c.Evaluator == SelectorSide.ContentField))
            data.Add(c.Selector);
        return data;
    }

    public static TheoryData<string> NodeSideCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in SelectorResolutionCorpus.Cases.Where(c => c.Evaluator == SelectorSide.NodeField))
            data.Add(c.Selector);
        return data;
    }

    /// <summary>
    /// A node whose content carries <see cref="FromContent"/> exactly where
    /// <paramref name="selector"/> would look for it — under the selector's own name, with a
    /// leading <c>content.</c> stripped and any remaining dots nested.
    ///
    /// <para>The content is an UNTYPED <see cref="JsonElement"/> on purpose: that is what a real
    /// mesh hands the evaluator whenever the polymorphic converter cannot resolve a <c>$type</c>,
    /// and it exercises the case-insensitive JSON lookup rather than reflection.</para>
    /// </summary>
    private static MeshNode NodeWithContentAt(string selector)
    {
        var path = selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase)
            ? selector["content.".Length..]
            : selector;

        var parts = path.Split('.');
        JsonNode leaf = JsonValue.Create(FromContent)!;
        for (var i = parts.Length - 1; i >= 0; i--)
            leaf = new JsonObject { [parts[i]] = leaf };

        return new MeshNode("Offer", "Crm")
        {
            Content = JsonSerializer.Deserialize<JsonElement>(leaf.ToJsonString()),
        };
    }

    private static JsonElement UntypedContent(params (string Key, string Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
            obj[key] = JsonValue.Create(value);
        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
    }
}
