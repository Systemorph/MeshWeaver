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
///
/// <para><b><c>*</c> is the only wildcard.</b> The <c>content.code</c> cases put SQL's LIKE
/// metacharacters (<c>%</c>, <c>_</c> and the escape character <c>\</c>) into a user value beside a
/// wildcard, each with a decoy node that matches only if the executor reads the character as a
/// metacharacter. A SQL generator must escape them and translate only <c>*</c>; the in-memory glob
/// (<c>QueryWildcard.IsMatch</c>) already reads them literally.</para>
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
        // `*` is the ONLY wildcard (each pair: the value a literal reading must match, and the decoy
        // that a SQL reading of `%` / `_` / `\` as LIKE metacharacters would match as well).
        new("pct", "Epsilon", """{"code":"50%off"}"""),
        new("pct-decoy", "Zeta", """{"code":"50xoff"}"""),
        new("under", "Eta", """{"code":"a_b"}"""),
        new("under-decoy", "Theta", """{"code":"axb"}"""),
        new("backslash", "Iota", """{"code":"c\\d"}"""),
        new("backslash-decoy", "Kappa", """{"code":"cd"}"""),
        // A QUOTED value is LITERAL (#6634): `|` and `*` inside the quotes are ordinary characters.
        // Each decoy is what a parser that still reads them as alternation / wildcard would return.
        new("pipe", "Lambda", """{"code":"a|b"}"""),
        new("pipe-decoy", "Mu", """{"code":"a"}"""),
        new("star", "Nu", """{"code":"x*y"}"""),
        new("star-decoy", "Xi", """{"code":"xzzy"}"""),
    ];

    private static ImmutableArray<string> Ids(params string[] ids) => [.. ids];

    /// <summary>Every case, each negation beside its positive twin.</summary>
    public static readonly ImmutableArray<NegationCase> Cases =
    [
        new("content.apiKey:enc*", Ids("enc-lower", "enc-upper"),
            "Positive prefix wildcard, case-insensitive on both executors (ILIKE / glob)."),
        new("-content.apiKey:enc*", Ids("plain", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "THE DEFECT: the negated wildcard must be the complement — including nodes that lack the field."),
        new("content.apiKey:*ccc", Ids("plain"),
            "Positive suffix wildcard."),
        new("-content.apiKey:*ccc", Ids("enc-lower", "enc-upper", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "Negated suffix wildcard."),
        new("content.kind:secret", Ids("enc-lower", "enc-upper"),
            "Positive non-wildcard content field."),
        new("-content.kind:secret", Ids("plain", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "Control: the negated NON-wildcard content field — NotEqual, null-inclusive."),
        new("name:foo", Ids("no-content"),
            "Positive plain node field."),
        new("-name:foo", Ids("enc-lower", "enc-upper", "plain", "no-key", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "Control: the negated plain node field."),
        new("-content.apiKey:enc* -name:foo", Ids("plain", "no-key", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "Two negations AND together."),
        new("content.code:50%*", Ids("pct"),
            "`%` is LITERAL — only `*` is a wildcard. Read as SQL's `%` it would match `50xoff` too."),
        new("-content.code:50%*", Ids("enc-lower", "enc-upper", "plain", "no-key", "no-content", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "The negated form: `%` literal there too, so the decoy is kept."),
        new("content.code:a_*", Ids("under"),
            "`_` is LITERAL. Read as SQL's single-character `_` it would match `axb` too."),
        new("-content.code:a_*", Ids("enc-lower", "enc-upper", "plain", "no-key", "no-content", "pct", "pct-decoy", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "The negated form of the literal `_`."),
        new(@"content.code:c\d*", Ids("backslash"),
            "The LIKE escape character is LITERAL. Read as an escape, `c\\d%` is `cd%` — the decoy, not the value."),
        new(@"-content.code:c\d*", Ids("enc-lower", "enc-upper", "plain", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash-decoy", "pipe", "pipe-decoy", "star", "star-decoy"),
            "The negated form of the literal backslash."),
        new("content.code:\"a|b\"", Ids("pipe"),
            "A quoted `|` is LITERAL. Read as alternation it would match `a` (the decoy) and not `a|b`."),
        new("-content.code:\"a|b\"", Ids("enc-lower", "enc-upper", "plain", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe-decoy", "star", "star-decoy"),
            "The negated quoted literal `|`."),
        new("content.code:\"x*y\"", Ids("star"),
            "A quoted `*` is LITERAL. Read as a wildcard it would match `xzzy` (the decoy) too."),
        new("-content.code:\"x*y\"", Ids("enc-lower", "enc-upper", "plain", "no-key", "no-content", "pct", "pct-decoy", "under", "under-decoy", "backslash", "backslash-decoy", "pipe", "pipe-decoy", "star-decoy"),
            "The negated quoted literal `*` — a one-value NotIn, so no executor reads it as a glob."),
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
        ("content.code:50%*", "-content.code:50%*"),
        ("content.code:a_*", "-content.code:a_*"),
        (@"content.code:c\d*", @"-content.code:c\d*"),
        ("content.code:\"a|b\"", "-content.code:\"a|b\""),
        ("content.code:\"x*y\"", "-content.code:\"x*y\""),
    ];
}
