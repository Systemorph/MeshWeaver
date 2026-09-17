using System.Collections.Immutable;

namespace MeshWeaver.Compiler;

/// <summary>
/// ONE warning a NodeType compile produced, kept STRUCTURED all the way to whoever reports it.
///
/// <para>🚨 Why this is not a string. A warning used to reach every consumer already formatted —
/// <c>"CS1591: Missing XML comment … (line 42)"</c> — which is fine for an activity log a human
/// reads and useless for everything else: a build lane that wants to COUNT by diagnostic id, decide
/// a ratchet on (type, id), or fold the same diagnostic reported by eight NodeTypes into one line
/// would have to parse the prose back apart. Parsing a message to recover a field the producer
/// already had is how a report and a verdict drift; the id travels as an id.</para>
///
/// <para><see cref="Describe"/> renders exactly the string the formatted list has always carried,
/// so the compile ACTIVITY's wording is unchanged and this is purely a widening of what the
/// producer hands over.</para>
/// </summary>
/// <param name="Id">The diagnostic id — <c>CS1591</c>, <c>CS0219</c>, <c>CS1574</c>.</param>
/// <param name="Message">The compiler's own message text, verbatim.</param>
/// <param name="Line">1-based line in the GENERATED source tree, or 0 when the diagnostic carries
/// no source location. The generated source is one concatenated tree, so the line is the only
/// locator a reader gets — and it is a locator INSIDE that tree, never in the authored file.</param>
public readonly record struct CompileWarning(string Id, string Message, int Line)
{
    /// <summary>
    /// The one-line rendering — byte-identical to the string the formatted warning list has always
    /// carried, so the activity log's wording does not move when the structure arrives.
    /// </summary>
    public string Describe() =>
        Line > 0 ? $"{Id}: {Message} (line {Line})" : $"{Id}: {Message}";

    /// <summary>
    /// The SITE identity a report folds on: the id and the message, WITHOUT the line.
    ///
    /// <para>🚨 The line is deliberately excluded, and that is the whole of the de-duplication this
    /// exists for. A <c>Source/*.cs</c> shared by eight NodeTypes (<c>shared=@Lib/Source</c>) is
    /// concatenated into eight different generated trees, so ONE missing doc comment arrives as
    /// eight warnings at eight different line numbers. Folding on (id, message) reports it once;
    /// folding on the line reports it eight times and tells the reader there are eight defects.
    /// The message names the member (<c>… for publicly visible type or member 'Foo.Bar'</c>), so it
    /// identifies the site more honestly than a line in a tree nobody has on disk.</para>
    /// </summary>
    public (string Id, string Message) Site => (Id, Message);

    /// <summary>The diagnostic id of a MISSING XML doc comment — the one warning class that gets
    /// its own baseline, because it is documentation debt rather than a latent bug. Named here, on
    /// the type that carries the id, so the producer and every ratchet spell it once.</summary>
    public const string MissingDocComment = "CS1591";

    /// <summary>
    /// Whether this warning is a missing doc comment (<see cref="MissingDocComment"/>).
    ///
    /// <para>🚨 ONLY <c>CS1591</c>, deliberately. The neighbouring doc diagnostics are NOT
    /// documentation debt and must not share its baseline: <c>CS1574</c>/<c>CS1584</c> (a
    /// <c>cref</c> that resolves to nothing), <c>CS1572</c> (a <c>&lt;param&gt;</c> tag naming a
    /// parameter that does not exist) and <c>CS1734</c> (the same for <c>&lt;paramref&gt;</c>) are
    /// doc comments that EXIST and are WRONG — they are the exact breakage a cross-repo move
    /// causes, and they belong with the latent bugs.</para>
    /// </summary>
    public bool IsMissingDocComment =>
        string.Equals(Id, MissingDocComment, StringComparison.Ordinal);

    /// <summary>
    /// The diagnostic ids an in-mesh compile does NOT report — because no <c>dotnet build</c> in
    /// this fleet reports them either.
    ///
    /// <para>🚨 <b>This is a PARITY list, not an escape hatch.</b> The whole premise of the in-mesh
    /// warning standard is that in-mesh C# is held to the standard <c>src/</c> C# is held to under
    /// <c>-warnaserror</c>. A raw <see cref="Microsoft.CodeAnalysis.CSharp.CSharpCompilation"/>
    /// applies no <c>NoWarn</c> at all, so without this list the in-mesh compile is held to a
    /// standard that is not merely equal to <c>src/</c>'s but STRICTER — and it was, in exactly two
    /// families, both of which are recorded below with the file that suppresses them for
    /// <c>src/</c>. A code that is NOT suppressed for <c>src/</c> must never be added here; it gets
    /// fixed, or it is recorded as debt in a <c>--warning-baseline</c>.</para>
    ///
    /// <para><b>Reference-set skew — <c>CS1701</c>, <c>CS1702</c>.</b> "Assuming assembly reference
    /// 'A, Version=X' … matches identity 'A, Version=Y'". A property of the REFERENCE SET, never of
    /// the content: <c>System.Reactive</c> is built against .NET 8's <c>System.Linq.Expressions</c>
    /// and runs on .NET 10, so every NodeType that touches Rx earns one. No author can fix it and no
    /// <c>#pragma</c> belongs in their source. The .NET SDK has always suppressed both by default —
    /// <c>Microsoft.NET.Sdk.CSharp.props</c>: <c>&lt;NoWarn Condition=" '$(NoWarn)' == ''
    /// "&gt;1701;1702&lt;/NoWarn&gt;</c> — so every <c>src/</c> project in the fleet is already
    /// blind to them, and the 95 baseline entries this list retires were the in-mesh compile being
    /// the only compiler in the fleet that saw them.</para>
    ///
    /// <para><b>Doc COMPLETENESS — <c>CS1591</c>, <c>CS1573</c>, <c>CS1712</c>.</b> A public member
    /// with no doc comment, a parameter with no <c>&lt;param&gt;</c> tag, a type parameter with no
    /// <c>&lt;typeparam&gt;</c> tag. Nothing is broken; the member simply is not described. Core
    /// suppresses exactly these three for <c>src/</c> (<c>Directory.Build.props</c>,
    /// <c>&lt;NoWarn&gt;…CS1591;CS1573;CS1712&lt;/NoWarn&gt;</c>), mirrored verbatim in
    /// <c>MeshWeaver.Plugins/src/</c> and <c>MeshWeaver.SocialMedia/src/</c>.</para>
    ///
    /// <para>🚨 The doc comments that EXIST and are WRONG stay reported and stay ERRORS —
    /// <c>CS1570</c> (malformed XML), <c>CS1571</c> (duplicate <c>&lt;param&gt;</c>),
    /// <c>CS1572</c>/<c>CS1734</c> (a tag naming a parameter that is not there),
    /// <c>CS1574</c>/<c>CS1584</c>/<c>CS0419</c> (a <c>cref</c> that resolves to nothing, or to two
    /// things), <c>CS1587</c> (a doc comment on something that cannot carry one). A <c>cref</c>
    /// pointing at a type that moved is a broken link in shipped API documentation, which is the
    /// exact breakage a cross-repo move causes.</para>
    /// </summary>
    public static readonly ImmutableHashSet<string> NotReported =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "CS1701", "CS1702",                 // reference-set skew — the SDK's own default NoWarn
            "CS1591", "CS1573", "CS1712");      // doc COMPLETENESS — core's src/ NoWarn

    /// <summary>
    /// Whether <paramref name="id"/> is one the in-mesh compile does not report — see
    /// <see cref="NotReported"/>.
    /// </summary>
    /// <param name="id">The diagnostic id, e.g. <c>CS1701</c>.</param>
    public static bool IsNotReported(string id) => NotReported.Contains(id);
}
