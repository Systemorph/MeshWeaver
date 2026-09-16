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
    /// <c>cref</c> that resolves to nothing) and <c>CS1573</c> (a <c>&lt;param&gt;</c> tag naming a
    /// parameter that does not exist) are doc comments that EXIST and are WRONG — they are the
    /// exact breakage a cross-repo move causes, and they belong with the latent bugs.</para>
    /// </summary>
    public bool IsMissingDocComment =>
        string.Equals(Id, MissingDocComment, StringComparison.Ordinal);
}
