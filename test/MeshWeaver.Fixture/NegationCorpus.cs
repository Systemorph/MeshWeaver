using System.Collections.Immutable;

namespace MeshWeaver.Fixture;

/// <summary>One node of the <see cref="NegationCorpus"/> fixture set.</summary>
/// <param name="Id">The node id (its path is <c>{namespace}/{Id}</c> wherever a suite seeds it).</param>
/// <param name="Name">The node's own <c>Name</c> — the plain node-field control.</param>
/// <param name="ContentJson">The node's content as JSON, or <see langword="null"/> for a node with no content at all.</param>
public sealed record NegationNode(string Id, string Name, string? ContentJson);

/// <summary>One filter and the EXACT set of fixture ids it must return.</summary>
/// <param name="Filter">The filter as a query spells it — WITHOUT any scope; each suite adds its own anchor.</param>
/// <param name="Expected">The ids of <see cref="NegationCorpus.Nodes"/> that must match, and no others.</param>
/// <param name="Note">Why this case is in the corpus.</param>
public sealed record NegationCase(string Filter, ImmutableArray<string> Expected, string Note);

/// <summary>
/// 🔴 <b>Negation is the COMPLEMENT within the same scope — on BOTH query executors
/// (Systemorph/MeshWeaver#6581).</b>
///
/// <para>A negated WILDCARD filter (<c>-content.apiKey:enc*</c>) was a no-op on both
/// <c>QueryEvaluator</c> (core) and <c>PostgreSqlSqlGenerator</c> (MeshWeaver.Plugins): the parser
/// kept it as <c>NotEqual</c> with the literal value <c>enc*</c> and each executor compared that
/// literally, so the negation matched EVERY node — the ones it was written to exclude included.
/// Measured on a live portal: 11 ModelProviders, 6 match <c>content.apiKey:enc*</c>, and the
/// negation returned all 11.</para>
///
/// <para>Like <see cref="SelectorResolutionCorpus"/> this is pure data in a project both repositories
/// reference: core runs every case through <c>QueryParser</c> + <c>QueryEvaluator</c>; Plugins seeds
/// the same nodes into Postgres and runs every case through <c>PostgreSqlMeshQuery</c>. Each
/// negated case sits beside its positive twin, and <see cref="Complements"/> lets a suite assert the
/// two partition the node set — so an executor that answers a negation with "everything" or with
/// "nothing" fails by construction.</para>
///
/// <para><b>Missing field semantics.</b> A node that LACKS the field (or has no content) does not
/// match <c>field:val*</c>, so <c>-field:val*</c> INCLUDES it. That is the rule SQL's
/// <c>NotEqual</c>/<c>NotIn</c> already followed (<c>… OR sel IS NULL</c>), now stated for the
/// wildcard form too.</para>
/// </summary>
public static class NegationCorpus
{
    /// <summary>The node type every fixture node carries, so a suite can anchor on it.</summary>
    public const string NodeType = "NegationProbe";

    /// <summary>The fixture set. Every content field is a string, so both executors compare text.</summary>
    public static readonly ImmutableArray<NegationNode> Nodes =
    [
        new("enc-lower", "Alpha", """{"apiKey":"enc:aaa","kind":"secret"}"""),
        new("enc-upper", "Beta", """{"apiKey":"ENC:bbb","kind":"secret"}"""),
        new("plain", "Gamma", """{"apiKey":"plain-ccc","kind":"public"}"""),
        new("no-key", "Delta", """{"kind":"public"}"""),
        new("no-content", "foo", null),
    ];

    private static ImmutableArray<string> Ids(params string[] ids) => [.. ids];

    /// <summary>Every case, each negation beside its positive twin.</summary>
    public static readonly ImmutableArray<NegationCase> Cases =
    [
        new("content.apiKey:enc*", Ids("enc-lower", "enc-upper"),
            "Positive prefix wildcard, case-insensitive on both executors (ILIKE / glob)."),
        new("-content.apiKey:enc*", Ids("plain", "no-key", "no-content"),
            "THE DEFECT: the negated wildcard must be the complement — including nodes that lack the field."),
        new("content.apiKey:*ccc", Ids("plain"),
            "Positive suffix wildcard."),
        new("-content.apiKey:*ccc", Ids("enc-lower", "enc-upper", "no-key", "no-content"),
            "Negated suffix wildcard."),
        new("content.kind:secret", Ids("enc-lower", "enc-upper"),
            "Positive non-wildcard content field."),
        new("-content.kind:secret", Ids("plain", "no-key", "no-content"),
            "Control: the negated NON-wildcard content field — NotEqual, null-inclusive."),
        new("name:foo", Ids("no-content"),
            "Positive plain node field."),
        new("-name:foo", Ids("enc-lower", "enc-upper", "plain", "no-key"),
            "Control: the negated plain node field."),
        new("-content.apiKey:enc* -name:foo", Ids("plain", "no-key"),
            "Two negations AND together."),
    ];

    /// <summary>
    /// Pairs of (positive, negated) filters whose results must PARTITION <see cref="Nodes"/>: disjoint,
    /// and together every node. A suite asserts this on its executor's ACTUAL answers, not on
    /// <see cref="Cases"/>, so a stale expectation cannot hide a broken negation.
    /// </summary>
    public static readonly ImmutableArray<(string Positive, string Negated)> Complements =
    [
        ("content.apiKey:enc*", "-content.apiKey:enc*"),
        ("content.apiKey:*ccc", "-content.apiKey:*ccc"),
        ("content.kind:secret", "-content.kind:secret"),
        ("name:foo", "-name:foo"),
    ];
}
