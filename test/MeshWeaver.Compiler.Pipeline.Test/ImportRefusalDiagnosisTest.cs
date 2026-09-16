using System.Collections.Immutable;
using System.Linq;
using Xunit;
using Lsp = MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The pure half of <see cref="ImportRefusalDiagnosis"/> (issue #4469) — no mesh, no hub. These are
/// the rules that decide whether a compile failure may be ATTRIBUTED to an import that lost a
/// source node, and every one of them exists to stop the attribution being made where it is not
/// established. Saying "an import dropped this" when it did not is worse than the silence #4469 is
/// about: it sends an operator to the repository for a file that was never there.
/// </summary>
public class ImportRefusalDiagnosisTest
{
    private const string RefusedPath = "Hosting/Deployment/Source/SelfUpdateRouting";

    private static ImmutableList<ImportRefusal> Recorded(string reason = "Postgres refused a 0x00 byte") =>
        ImmutableList.Create(new ImportRefusal(RefusedPath, reason));

    private static ImmutableList<ImportRefusalDiagnosis.DiagnosticEvidence> Unresolved(string symbol) =>
        ImmutableList.Create(new ImportRefusalDiagnosis.DiagnosticEvidence(
            "CS0246", $"The type or namespace name '{symbol}' could not be found (are you missing a using directive or an assembly reference?)"));

    // ---- the case the issue is about -----------------------------------------------------------

    /// <summary>
    /// 🚨 The measured shape, 2026-09-15: a refusal recorded for
    /// <c>Hosting/Deployment/Source/SelfUpdateRouting</c>, and a compile — of a DIFFERENT type in
    /// the same partition — failing with <c>CS0246 'SelfUpdateRouting'</c>.
    /// </summary>
    [Fact]
    public void ARecordedRefusalWhoseIdentifierIsUnresolved_Explains()
    {
        var explaining = ImportRefusalDiagnosis.Explaining(
            Recorded(), Unresolved("SelfUpdateRouting"));

        explaining.Should().NotBeNull();
        explaining!.Select(r => r.NodePath).Should().Equal([RefusedPath]);
    }

    /// <summary>The sentence an operator reads carries the CAUSE, the FILE and the REASON — the
    /// three things five parked Hosting NodeTypes never said between them.</summary>
    [Fact]
    public void TheDescription_NamesTheImport_TheFile_AndTheReason()
    {
        var text = ImportRefusalDiagnosis.Describe(Recorded("Postgres refused a 0x00 byte"));

        text.Should().NotBeNull();
        text!.Should().Contain("REFUSED BY AN IMPORT");
        text.Should().Contain(RefusedPath);
        text.Should().Contain("Postgres refused a 0x00 byte");
        text.Should().Contain("re-import",
            "the repair is in the REPOSITORY — a diagnosis that stops at the diagnosis leaves the "
            + "reader where the diagnostics already left them");
    }

    /// <summary>A refusal recorded before reasons were kept says so, rather than reading as a
    /// refusal with no reason at all.</summary>
    [Fact]
    public void ARefusalWithNoRecordedReason_SaysSoRatherThanInventingOne()
    {
        var text = ImportRefusalDiagnosis.Describe(
            ImmutableList.Create(new ImportRefusal(RefusedPath, null)));

        text!.Should().Contain(RefusedPath);
        text.Should().Contain("predates reason recording");
    }

    // ---- the cases that must report NOTHING ----------------------------------------------------

    /// <summary>
    /// 🚨 THE DENOMINATOR RULE. A bookkeeping read that did not come back is NOT "no import lost
    /// anything" — and the difference has to survive all the way to the node, or a reader will take
    /// an empty field for a measurement.
    /// </summary>
    [Fact]
    public void AnUnreadableLedger_IsNotDetermined_NeverEmpty() =>
        ImportRefusalDiagnosis.Explaining(null, Unresolved("SelfUpdateRouting"))
            .Should().BeNull(
                "null in, null out: 'I could not look' and 'I looked and found nothing' are "
                + "different facts, and only the second may be recorded as one");

    /// <summary>
    /// 🚨 THE GRANULARITY RULE — the trap #4467 had just removed from the import, one layer up. A
    /// partition with a standing refusal must not have it read onto every failed compile in it.
    /// </summary>
    [Fact]
    public void ARefusalThatDeclaresADifferentSymbol_ExplainsNothing()
    {
        var explaining = ImportRefusalDiagnosis.Explaining(
            Recorded(), Unresolved("SomethingElseEntirely"));

        explaining.Should().NotBeNull("the ledger WAS read — that is a measurement");
        explaining!.Should().BeEmpty(
            "one refused node may not speak for the whole partition: the unresolved name here was "
            + "never going to come from that file, so the cause is a deliberate deletion or a "
            + "module that is not loaded on this replica (MeshWeaver#3583) — two different fixes");
    }

    /// <summary>
    /// A failure that is not about a NAME cannot be caused by a missing file, so no refusal
    /// explains it however many the partition records.
    /// </summary>
    [Fact]
    public void AFailureThatIsNotAnUnresolvedName_ExplainsNothing()
    {
        var notAName = ImmutableList.Create(new ImportRefusalDiagnosis.DiagnosticEvidence(
            "CS0029", "Cannot implicitly convert type 'SelfUpdateRouting' to 'int'"));

        ImportRefusalDiagnosis.Explaining(Recorded(), notAName).Should().BeEmpty(
            "the symbol RESOLVED here — it is named in a conversion error. Widening the diagnosis "
            + "to every diagnostic that mentions a refused file's name would fire on code that is "
            + "merely wrong, and a signal that fires on everything is ignored");
    }

    /// <summary>
    /// A refused node that is not a C# source file declares no symbol. A markdown page called
    /// <c>Deployment</c> must never be offered as the reason a type called <c>Deployment</c> will
    /// not resolve.
    /// </summary>
    [Fact]
    public void ARefusedNonSourceNode_ExplainsNothing()
    {
        var page = ImmutableList.Create(new ImportRefusal("Hosting/Deployment", "refused"));

        ImportRefusalDiagnosis.Explaining(page, Unresolved("Deployment")).Should().BeEmpty(
            "nothing under a Source/ or Test/ segment, so nothing that could have declared a C# "
            + "symbol — this is an accusation from evidence nobody has");
    }

    /// <summary>A SUBSTRING hit is not a mention: a refused <c>Log</c> must not claim every
    /// <c>CS0246</c> about <c>Logger</c>.</summary>
    [Fact]
    public void ASubstringOfALongerIdentifier_IsNotAMention()
    {
        var shortName = ImmutableList.Create(new ImportRefusal("P/Lib/Source/Log", "refused"));

        ImportRefusalDiagnosis.Explaining(shortName, Unresolved("Logger")).Should().BeEmpty();
        ImportRefusalDiagnosis.Explaining(shortName, Unresolved("Log")).Should().NotBeEmpty(
            "…while the whole-word hit still counts, or the guard would silence the real case too");
    }

    // ---- the evidence reader --------------------------------------------------------------------

    /// <summary>
    /// The structured diagnostics are preferred, and only ERRORS count — a warning that happens to
    /// mention the name is not a failure to explain.
    /// </summary>
    [Fact]
    public void StructuredDiagnostics_AreReadErrorsOnly()
    {
        var evidence = ImportRefusalDiagnosis.EvidenceOf(
        [
            new Lsp.DiagnosticInfo("CS0246", Lsp.DiagnosticSeverity.Error, "'A' could not be found", null),
            new Lsp.DiagnosticInfo("CS1591", Lsp.DiagnosticSeverity.Warning, "missing XML comment on 'A'", null),
        ], failureText: "ignored when structured rows exist");

        evidence.Select(e => e.Id).Should().Equal(["CS0246"]);
    }

    /// <summary>
    /// The emit path throws a <c>CompilationException</c> whose message is
    /// <c>CompileDiagnostics.FormatCompileFailure</c>'s rendering, so the flat text has to be read
    /// too — PER LINE, so an unrelated <c>CS0246</c> elsewhere in the transcript cannot lend its ID
    /// to a mention of the symbol somewhere else.
    /// </summary>
    [Fact]
    public void AFlatFailureTranscript_IsReadPerLine()
    {
        var evidence = ImportRefusalDiagnosis.EvidenceOf(
            diagnostics: null,
            failureText:
                "Compilation failed for P/T:\n"
                + "CS0246 Error (line 3): The type or namespace name 'SelfUpdateRouting' could not be found\n"
                + "CS0029 Error (line 9): Cannot implicitly convert type 'int' to 'string'\n");

        evidence.Select(e => e.Id).Should().Equal(["CS0246", "CS0029"]);
        ImportRefusalDiagnosis.Explaining(Recorded(), evidence).Should().NotBeEmpty();

        // …and the per-line binding is what makes that precise: the ID and the identifier must come
        // from the SAME diagnostic.
        var split = ImportRefusalDiagnosis.EvidenceOf(
            diagnostics: null,
            failureText:
                "CS0246 Error (line 3): The type or namespace name 'SomethingElse' could not be found\n"
                + "CS0029 Error (line 9): Cannot convert 'SelfUpdateRouting' to 'int'\n");
        ImportRefusalDiagnosis.Explaining(Recorded(), split).Should().BeEmpty(
            "the unresolved-name diagnostic names a different symbol, and the one that names this "
            + "symbol is not an unresolved name — a flat search over the whole transcript would "
            + "have joined them and accused the import");
    }

    /// <summary>The identifier is the node's own id, with an extension dropped — a source node is
    /// named for the type it declares, which is the entire basis of the join.</summary>
    [Theory]
    [InlineData("Hosting/Deployment/Source/SelfUpdateRouting", "SelfUpdateRouting")]
    [InlineData("Hosting/Deployment/Source/SelfUpdateRouting.cs", "SelfUpdateRouting")]
    [InlineData("Bare", "Bare")]
    public void TheIdentifier_IsTheNodeId(string path, string expected) =>
        ImportRefusalDiagnosis.IdentifierOf(path).Should().Be(expected);

    /// <summary>Matched on a whole path SEGMENT, so a shared library counts as readily as a type's
    /// own subtree — four of the five parked Hosting types did not own the file they failed on.
    /// The LAST segment is the file itself and is never the marker.</summary>
    [Theory]
    [InlineData("P/T/Source/File", true)]
    [InlineData("P/T/Test/File", true)]
    [InlineData("Store/Core/Source/Deep/File", true)]
    [InlineData("P/T/Page", false)]
    [InlineData("P/Source", false)]
    [InlineData("P/Sources/File", false)]
    public void CodeSourcePaths_AreRecognisedBySegment(string path, bool expected) =>
        ImportRefusalDiagnosis.IsCodeSourcePath(path).Should().Be(expected);
}
