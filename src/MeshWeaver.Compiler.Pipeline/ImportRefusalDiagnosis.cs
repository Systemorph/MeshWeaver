using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lsp = MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 <b>Issue #4469 — joins a NodeType's UNRESOLVED-SYMBOL compile failure to the import that
/// could not write the source node defining it.</b>
///
/// <para><b>What was dark.</b> An import that cannot write one source node leaves the partition
/// REFERENCED-BUT-INCOMPLETE: the files that reference the missing symbol DID land, so every
/// NodeType built from them fails to compile, and the only thing an operator sees is
/// <c>CS0246: The type or namespace name 'SelfUpdateRouting' could not be found</c> — on a symbol
/// whose file is plainly in git. Measured on <c>memex.systemorph.com</c>, 2026-09-15: a literal NUL
/// byte in <c>Hosting/Deployment/Source/SelfUpdateRouting.cs</c> is unstorable in a Postgres text
/// column, that one row was refused while forty landed, and five Hosting NodeTypes parked —
/// <c>Hosting/InstanceAction</c> among them, so NO instance action ran on the control instance at
/// all. #4467 fixed the import half (the refusal is remembered per node, with its reason, and the
/// sync activity names the file). This is the other direction: the operator who starts from the
/// COMPILE ERROR.</para>
///
/// <para><b>🚨 The denominator, which is the whole difficulty.</b> A symbol can be unresolved for
/// three reasons that look identical from here: an import lost the file, the file was legitimately
/// DELETED, or the module carrying it is not loaded on this replica (a declined bundle,
/// MeshWeaver#3583 — a different defect with a different fix). Saying "an import dropped this" when
/// it did not is worse than today's silence, so an accusation is made only where BOTH halves are
/// established from facts in hand:</para>
/// <list type="number">
///   <item>the partition's import bookkeeping RECORDS a refusal for a node that is a C# source node
///     (<see cref="IPartitionImportRefusals"/> answered — not "could not answer" —, and the refused
///     path sits under a <c>Source</c>/<c>Test</c> segment, so it is a file that defines symbols);
///     AND</item>
///   <item>THIS compile failed with a name-resolution diagnostic (<see cref="UnresolvedNameIds"/>)
///     that names that node's identifier as a whole word.</item>
/// </list>
/// <para>Either half alone reports NOTHING. That is what scopes the finding to the types that
/// actually reference the missing node rather than to every type in the partition — the over-broad
/// granularity #4467 had just removed from the import, which must not be re-created one layer
/// up.</para>
///
/// <para><b>Why the identifier and not the message.</b> The join reads the refused node's own ID out
/// of its path and looks for it inside the diagnostic — never the reverse. Roslyn's message text is
/// LOCALIZED and its wording is not a contract; a C# identifier is neither. The diagnostic ID
/// ( <c>CS0246</c> &amp;c.) is a stable wire identifier and is not translated. So the join holds on a
/// portal running in any language.</para>
/// </summary>
public static class ImportRefusalDiagnosis
{
    /// <summary>
    /// The compiler diagnostics that mean "a NAME could not be resolved" — the only failures a
    /// missing source node can produce, and therefore the only ones an import refusal may be offered
    /// as an explanation for.
    ///
    /// <para>Deliberately NOT widened to the whole "something is wrong with a type" family
    /// (<c>CS1061</c> and friends): a missing extension method has many more causes than a missing
    /// file, and a diagnosis that fires on those would teach its reader to ignore it. Wire
    /// identifiers — never translated.</para>
    /// </summary>
    public static readonly ImmutableHashSet<string> UnresolvedNameIds = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        // The type or namespace name 'X' could not be found.
        "CS0246",
        // The name 'X' does not exist in the current context.
        "CS0103",
        // The type or namespace name 'X' does not exist in the namespace 'N'.
        "CS0234",
        // The type name 'X' does not exist in the type 'T'.
        "CS0426",
        // The type or namespace name 'X' could not be found in the global namespace.
        "CS0400");

    /// <summary>
    /// One compiler diagnostic reduced to what the join needs: its (untranslated) ID and the text
    /// the symbol name appears in.
    /// </summary>
    /// <param name="Id">The diagnostic ID, e.g. <c>CS0246</c>.</param>
    /// <param name="Message">The diagnostic's message.</param>
    public readonly record struct DiagnosticEvidence(string Id, string Message);

    /// <summary>
    /// The evidence of THIS compile, from the structured diagnostics when Roslyn produced them and
    /// otherwise from the flat failure text.
    ///
    /// <para>Both shapes occur: <c>NodeCompilationResult.Diagnostics</c> carries the structured rows
    /// for an ordinary compile failure, while a <c>CompilationException</c> from the emit path
    /// carries <c>CompileDiagnostics.FormatCompileFailure</c>'s rendering, one
    /// <c>"{Id} {Severity} (line N): {message}"</c> per line. Parsing the second per LINE — rather
    /// than searching one flat haystack — keeps the ID and the identifier bound to the SAME
    /// diagnostic, so an unrelated <c>CS0246</c> elsewhere in the transcript cannot lend its ID to a
    /// mention of the symbol somewhere else.</para>
    /// </summary>
    /// <param name="diagnostics">The compile's structured diagnostics, when it produced any.</param>
    /// <param name="failureText">The flat failure summary, used only when there are no structured
    /// diagnostics.</param>
    public static ImmutableList<DiagnosticEvidence> EvidenceOf(
        IReadOnlyList<Lsp.DiagnosticInfo>? diagnostics, string? failureText)
    {
        if (diagnostics is { Count: > 0 })
            return diagnostics
                .Where(d => d.Severity == Lsp.DiagnosticSeverity.Error)
                .Select(d => new DiagnosticEvidence(d.Id ?? string.Empty, d.Message ?? string.Empty))
                .ToImmutableList();

        if (string.IsNullOrWhiteSpace(failureText))
            return ImmutableList<DiagnosticEvidence>.Empty;

        var builder = ImmutableList.CreateBuilder<DiagnosticEvidence>();
        foreach (var line in failureText.Split('\n'))
        {
            var id = LeadingDiagnosticId(line);
            if (id is not null)
                builder.Add(new DiagnosticEvidence(id, line));
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The <c>CS####</c> token a rendered diagnostic line starts with, or <see langword="null"/>.
    /// Pure — no regex, so nothing here can be quadratic on a pathological transcript.
    /// </summary>
    private static string? LeadingDiagnosticId(string line)
    {
        var text = line.AsSpan().TrimStart();
        if (text.Length < 3 || !(text[0] is 'C' or 'c') || !(text[1] is 'S' or 's'))
            return null;
        var end = 2;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        return end == 2 ? null : text[..end].ToString();
    }

    /// <summary>
    /// The refusals that EXPLAIN this compile's failure — the intersection described on the class.
    /// Pure and total.
    ///
    /// <para>🚨 <b>Three answers, never two.</b> <see langword="null"/> in gives
    /// <see langword="null"/> out: the import bookkeeping could not be read, so NOTHING was
    /// established and the caller must not record "no import lost anything". A non-null input
    /// always yields a (possibly empty) list, which is the genuinely measured "checked, and it
    /// explains nothing — look at a deliberate deletion, or at a module that is not loaded on this
    /// replica".</para>
    /// </summary>
    /// <param name="refusals">What the partition's import bookkeeping records, or
    /// <see langword="null"/> when it could not be read.</param>
    /// <param name="evidence">This compile's diagnostics (see <see cref="EvidenceOf"/>).</param>
    public static ImmutableList<ImportRefusal>? Explaining(
        ImmutableList<ImportRefusal>? refusals, IReadOnlyList<DiagnosticEvidence> evidence)
    {
        if (refusals is null)
            return null;

        var unresolved = evidence
            .Where(e => UnresolvedNameIds.Contains(e.Id))
            .ToImmutableList();
        if (unresolved.Count == 0)
            return ImmutableList<ImportRefusal>.Empty;

        return refusals
            .Where(r => IsCodeSourcePath(r.NodePath))
            .Where(r => IdentifierOf(r.NodePath) is { Length: > 0 } id
                        && unresolved.Any(e => MentionsIdentifier(e.Message, id)))
            .ToImmutableList();
    }

    /// <summary>
    /// Whether <paramref name="nodePath"/> is a C# SOURCE node — one whose absence can make a name
    /// unresolvable. A refused markdown page named <c>Deployment</c> defines no symbol, and offering
    /// it as the reason a type called <c>Deployment</c> will not resolve would be exactly the
    /// unfounded accusation this class exists to avoid. Matched on a whole path SEGMENT, so shared
    /// libraries (<c>Store/Core/Source/X</c>) count as readily as a type's own subtree.
    /// </summary>
    public static bool IsCodeSourcePath(string? nodePath)
    {
        if (string.IsNullOrEmpty(nodePath))
            return false;
        var segments = nodePath.Split('/');
        // The LAST segment is the file itself, never the marker — so stop one short.
        for (var i = 0; i < segments.Length - 1; i++)
            if (string.Equals(segments[i], CodeConventions.SourceSubNamespace, StringComparison.Ordinal)
                || string.Equals(segments[i], CodeConventions.TestSubNamespace, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// The C# identifier a source node would have defined — its node ID, with a file extension
    /// dropped if the path carries one. Pure.
    /// </summary>
    public static string IdentifierOf(string nodePath)
    {
        var slash = nodePath.LastIndexOf('/');
        var id = slash < 0 ? nodePath : nodePath[(slash + 1)..];
        var dot = id.LastIndexOf('.');
        return dot > 0 ? id[..dot] : id;
    }

    /// <summary>
    /// Whether <paramref name="message"/> names <paramref name="identifier"/> as a WHOLE word — a
    /// substring hit would let a refused <c>Log</c> claim every <c>CS0246</c> about <c>Logger</c>.
    /// Pure.
    /// </summary>
    public static bool MentionsIdentifier(string? message, string identifier)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(identifier))
            return false;
        var from = 0;
        while (true)
        {
            var at = message.IndexOf(identifier, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var beforeOk = at == 0 || !IsIdentifierChar(message[at - 1]);
            var after = at + identifier.Length;
            var afterOk = after >= message.Length || !IsIdentifierChar(message[after]);
            if (beforeOk && afterOk)
                return true;
            from = at + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The sentence a failed compile records when an import refusal explains it — or
    /// <see langword="null"/> when nothing was established, in which case the compile reports
    /// exactly what it reported before this existed.
    ///
    /// <para>🚨 It leads the recorded <c>CompilationError</c> for the same reason
    /// <see cref="SourceCoverage.Describe"/> does (#3903): a reader who is not told the file is
    /// MISSING spends the investigation hunting the named symbol through module surfaces that never
    /// carried it. It names the import as the cause, the file, the reason the write path gave, and
    /// the repair — because the repair is in the REPOSITORY, not in the code the diagnostics point
    /// at.</para>
    ///
    /// <para>Not localized, deliberately and consistently with its neighbour: this string is BAKED
    /// into the node at write time and read later by <c>get_diagnostics</c>, by
    /// <c>search content.compilationStatus:Error</c> and by the Progress page, so there is no viewer
    /// to follow at the moment it is composed. The line an operator READS in their own language is
    /// the compile activity's, which carries a catalog key and its arguments and is resolved per
    /// viewer at render time.</para>
    /// </summary>
    /// <param name="explaining">The refusals <see cref="Explaining"/> established.</param>
    public static string? Describe(IReadOnlyList<ImportRefusal>? explaining)
    {
        if (explaining is not { Count: > 0 })
            return null;
        const int Named = 5;
        var files = string.Join("; ", explaining
                .Take(Named)
                .Select(r => r.Reason is { Length: > 0 } reason
                    ? $"'{r.NodePath}' ({reason})"
                    : $"'{r.NodePath}' (the refusal predates reason recording)"))
            + (explaining.Count > Named ? $", … (+{explaining.Count - Named} more)" : "");
        return $"REFUSED BY AN IMPORT: {explaining.Count} source node(s) this compile needs were "
             + "declared by the partition's source and could NOT be written to the mesh, so the "
             + "unresolved name(s) below are missing FILES, not missing modules and not a mistake in "
             + $"the code that references them: {files}. No framework or module change can supply "
             + "them — fix the source file in the repository and re-import (the partition's import "
             + "manifest is what records this).";
    }

    /// <summary>
    /// Asks the partition's import bookkeeping whether it explains this compile failure. ONE
    /// emission, never a fault, always promptly — a diagnosis must not be able to delay or lose the
    /// terminal status write it rides on.
    ///
    /// <para>Emits <see langword="null"/> — NOT DETERMINED — whenever the question was not put or
    /// could not be answered: the failure was not about an unresolved NAME (asked first, so the
    /// common failure costs no mesh read at all), the mesh keeps no import bookkeeping (a local
    /// mesh, CI's disposable meshes, the bake host — no <see cref="IPartitionImportRefusals"/> is
    /// registered), or the read did not come back. Each reports nothing, exactly as before this
    /// existed.</para>
    /// </summary>
    /// <param name="hub">The hub to resolve the seam from and issue the read on.</param>
    /// <param name="nodeTypePath">The NodeType whose compile failed; its first segment is the
    /// partition asked about.</param>
    /// <param name="diagnostics">The compile's structured diagnostics, when it produced any.</param>
    /// <param name="failureText">The flat failure summary, used when there are none.</param>
    /// <param name="logger">Optional.</param>
    public static IObservable<ImmutableList<ImportRefusal>?> ForFailedCompile(
        IMessageHub hub,
        string nodeTypePath,
        IReadOnlyList<Lsp.DiagnosticInfo>? diagnostics,
        string? failureText,
        ILogger? logger = null)
    {
        var evidence = EvidenceOf(diagnostics, failureText);
        // 🚨 Asked FIRST, so the common case — a compile that failed on something other than an
        // unresolved name — costs no mesh read at all.
        if (!evidence.Any(e => UnresolvedNameIds.Contains(e.Id)))
            return Observable.Return<ImmutableList<ImportRefusal>?>(null);

        var provider = hub.ServiceProvider.GetService<IPartitionImportRefusals>();
        if (provider is null)
            return Observable.Return<ImmutableList<ImportRefusal>?>(null);

        var slash = nodeTypePath.IndexOf('/');
        var partition = slash > 0 ? nodeTypePath[..slash] : nodeTypePath;

        return provider.Refusals(partition)
            .Take(1)
            .Select(refusals => Explaining(refusals, evidence))
            .Catch((Exception ex) =>
            {
                logger?.LogDebug(ex,
                    "Compile: could not ask {Partition}'s import bookkeeping about the unresolved "
                    + "name(s) in {HubPath}'s failure; the failure is reported without an import "
                    + "verdict.", partition, nodeTypePath);
                return Observable.Return<ImmutableList<ImportRefusal>?>(null);
            })
            // Totality: the terminal compile write runs in this observable's OnNext, so an upstream
            // that completed empty must not be able to skip it. The default is the NOT-DETERMINED
            // value, so a silent upstream can never be read as a measurement.
            .DefaultIfEmpty(null);
    }
}
