using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// The Roslyn parse/compile/emit leg of a dynamic NodeType compile — canonical options, the
/// staged + verified disk emit, the in-memory emit, and the emit canary. Everything here
/// determines the emitted bytes given the shaped input, so it lives inside the toolchain
/// identity boundary (#1707). The scheduling (which thread, which timeout, which status
/// write-back) stays with the caller in MeshWeaver.Graph.
/// </summary>
public static class EmitPipeline
{
    /// <summary>The canonical parse options every dynamic NodeType compile uses — shared by the
    /// emit path, the LSP model, and the failure-diagnostics re-derivation so they can never
    /// diverge.</summary>
    internal static CSharpParseOptions CreateParseOptions()
        => new(documentationMode: DocumentationMode.Diagnose);

    /// <summary>
    /// The warnings a SUCCESSFUL compile produced, formatted for the compile ACTIVITY.
    ///
    /// <para>🚨 They used to be dropped on the floor. <c>emitResult.Diagnostics</c> is read only
    /// when <c>Success</c> is false, so on a green compile every warning the compiler produced was
    /// discarded — measured from the outside: a deliberate <c>CS0219</c> (an unused local) added to
    /// an in-mesh source compiled <c>ok</c> with zero warnings reported. That is the absence of a
    /// report, not a clean build, and it is why in-mesh C# was not held to the standard the
    /// compiled half is held to under <c>-warnaserror</c>: no unused-code warnings, and therefore
    /// no doc-comment or cref ones either, even though <see cref="CreateParseOptions"/> has always
    /// asked for <see cref="DocumentationMode.Diagnose"/>.</para>
    ///
    /// <para>Ordered and capped. A single bad using-directive can produce hundreds of identical
    /// diagnostics, and an activity log that is 400 lines of the same warning is one nobody reads —
    /// the cap is named in the last entry rather than applied silently.</para>
    /// </summary>
    internal static IReadOnlyList<string> Warnings(IEnumerable<Diagnostic> diagnostics)
    {
        var warnings = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning && !d.IsSuppressed)
            .Select(d => $"{d.Id}: {d.GetMessage()}{Where(d)}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();
        return warnings.Count <= MaxReportedWarnings
            ? warnings
            : warnings.Take(MaxReportedWarnings)
                .Append($"… and {warnings.Count - MaxReportedWarnings} more warning(s) not listed.")
                .ToList();
    }

    /// <summary>Where a diagnostic is, when the compiler knows — the generated source is one
    /// concatenated tree, so the line is the only locator a reader gets.</summary>
    private static string Where(Diagnostic diagnostic)
        => diagnostic.Location.IsInSource
            ? $" (line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1})"
            : string.Empty;

    /// <summary>How many distinct warnings reach the activity before the rest are counted instead
    /// of listed.</summary>
    internal const int MaxReportedWarnings = 50;

    /// <summary>The canonical compilation options every dynamic NodeType compile uses.</summary>
    internal static CSharpCompilationOptions CreateCompilationOptions()
        => new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithPlatform(Platform.AnyCpu);

    /// <summary>
    /// The canonical option set rendered for the CONTENT KEY (#1707 slice 4) — see
    /// <see cref="GeneratedInputIdentity.OptionsFingerprint"/>. It lives here, beside the three
    /// factories it renders, so an option added to one of them cannot be forgotten by the key:
    /// the rendering is REFLECTED, so a new option joins automatically.
    ///
    /// <para>Process-constant (a <see cref="Lazy{T}"/> of an immutable string): the options are
    /// literals, so this is a constant lookup rather than cached state.</para>
    /// </summary>
    internal static string OptionsFingerprint => _optionsFingerprint.Value;

    private static readonly Lazy<string> _optionsFingerprint = new(() =>
        GeneratedInputIdentity.OptionsFingerprint(
            CreateParseOptions(), CreateCompilationOptions(), DebugInformationFormat.PortablePdb));

    /// <summary>
    /// Builds the single-tree emit compilation for the generated source: parse with the source
    /// path and UTF-8 encoding embedded (critical for PDB source linking) + the canonical
    /// options.
    /// </summary>
    internal static CSharpCompilation CreateEmitCompilation(
        string source,
        string assemblyName,
        IEnumerable<MetadataReference> references,
        string parsePath,
        CancellationToken ct)
    {
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText, CreateParseOptions(), path: parsePath, cancellationToken: ct);
        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: CreateCompilationOptions());
    }

    /// <summary>
    /// Runs the real Roslyn emit for <paramref name="nodeName"/> into <paramref name="releaseDir"/>
    /// (dll + pdb + XML doc) and returns the DLL path it wrote TOGETHER WITH the digest of the image
    /// it produced. A failed emit throws a <see cref="CompilationException"/> carrying the formatted
    /// diagnostics.
    ///
    /// <para>🚨 It emits into memory and writes the bytes out, rather than streaming Roslyn straight
    /// at the file, for one reason: the publisher has to be able to prove the file on disk IS the
    /// image that was emitted. Streaming leaves nothing to compare against, which is how the old
    /// <c>Length &gt; 0</c> gate came to publish an artifact whose metadata had an unwritten region
    /// in it and PARK the NodeType for good (#1412). Peak memory is unchanged in practice — Roslyn
    /// already serialises the whole PE into an in-memory <c>BlobBuilder</c> before it writes a single
    /// byte. See <see cref="EmittedArtifact"/>.</para>
    ///
    /// <para>🚨 It does NOT log. A compile failure is reported EXACTLY ONCE, by the compile
    /// pipeline's single <c>.Catch&lt;…, CompilationException&gt;</c> funnel in
    /// <c>MeshNodeCompilationService</c> — the only place that also has the exception, its stack
    /// and the source-discovery report (which queries ran, which Code nodes matched). Logging the
    /// same diagnostics here as well double-counted EVERY compile failure in production: the ~150
    /// ERROR lines/24h across the production portals were ~72 real failures logged
    /// twice, and the duplicate came FIRST — context-free and exception-free, so red-log
    /// fingerprinting (which keys on category+eventId+exception+frame) filed it as a second,
    /// distinct fault whose only visible frame was the emit path. That is what made a plain
    /// "your C# does not compile" read like an emit/IO defect. <c>internal</c> so
    /// the log-once contract is unit-testable against a real broken compilation.</para>
    /// </summary>
    internal static EmittedArtifact EmitCompilationToDirectory(
        CSharpCompilation compilation, string nodeName, string nodePath, string releaseDir, CancellationToken ct)
    {
        var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
        var pdbPath = Path.Combine(releaseDir, $"{nodeName}.pdb");
        var xmlDocPath = Path.Combine(releaseDir, $"DynamicNode_{nodeName}.xml");

        using var dllImage = new MemoryStream();
        using var pdbImage = new MemoryStream();
        using var xmlDoc = new MemoryStream();

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: pdbPath);

        EmitResult emitResult;
        try
        {
            emitResult = compilation.Emit(
                dllImage, pdbImage, xmlDocumentationStream: xmlDoc,
                options: emitOptions, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roslyn THREW instead of returning diagnostics — an emit-phase fault, not a
            // compile error. Stamp the canary verdict on the exception and rethrow the
            // ORIGINAL untouched: the type name is what CI triage keys on, so this must
            // never become a wrapper. The verdict travels to the pipeline's single
            // reporting funnel via SummarizeCompileError — no second log from here, the
            // log-once contract above still holds.
            ex.Data[EmitCanaryDataKey] = ProbeSharedEmitState(compilation);
            throw;
        }

        if (!emitResult.Success)
            // Deterministic compile error — propagates straight out of the retry loop,
            // unlogged, to the pipeline's single reporting funnel.
            throw new CompilationException(nodePath,
                CompileDiagnostics.FormatCompileFailure(nodePath, emitResult.Diagnostics));

        // The DLL last: it is the discovery key of the release directory, so a reader that ever sees
        // the staging dir mid-write still finds the symbols and docs already beside it. (Publication
        // itself is the atomic Directory.Move in EmitToDiskWithRetry; this is belt and braces.)
        File.WriteAllBytes(pdbPath, Bytes(pdbImage));
        File.WriteAllBytes(xmlDocPath, Bytes(xmlDoc));
        var image = Bytes(dllImage);
        File.WriteAllBytes(dllPath, image);

        return EmittedArtifact.For(dllPath, image, Warnings(emitResult.Diagnostics));

        // Expandable MemoryStreams created with the parameterless ctor expose their buffer, so the
        // common path hands out a span over it instead of copying a multi-megabyte image.
        static ReadOnlySpan<byte> Bytes(MemoryStream s)
            => s.TryGetBuffer(out var seg) ? seg.AsSpan() : s.ToArray();
    }

    /// <summary>
    /// Key under which <see cref="EmitCompilationToDirectory"/> stamps the canary verdict on a
    /// thrown-from-Emit exception, and under which
    /// <c>NodeTypeCompilationHelpers.SummarizeCompileError</c> reads it back.
    /// </summary>
    internal const string EmitCanaryDataKey = "MeshWeaver.EmitCanary";

    /// <summary>
    /// Did the canary PROVE that this PROCESS can no longer emit — as opposed to this
    /// compilation's own inputs being at fault?
    ///
    /// <para>Both <c>REFERENCES</c> and <c>BELOW-ROSLYN</c> are reached only after the control
    /// compilation — trivial, freshly parsed, known-good source — ALSO failed to emit. Whatever
    /// broke, it is not the code the caller handed in, so a compile that aborts this way has
    /// formed NO verdict about that code. <c>NodeTypeCompilationHelpers.IsAvailabilityNonVerdict</c>
    /// reads this and stamps <c>CompilationStatus.Unavailable</c> instead of <c>Error</c>.</para>
    ///
    /// <para>🚨 The three withholding verdicts are deliberately false, for the same reason each of
    /// them exists:</para>
    /// <list type="bullet">
    ///   <item><c>OK</c> — the control emitted fine against the SAME references, so the fault IS a
    ///     property of this compilation's inputs. That is a genuine <c>Error</c>.</item>
    ///   <item><c>INCONCLUSIVE</c> — leg 2 never ran (no on-disk CoreLib to build the control
    ///     from), so nothing was proven either way. Reading "I could not run" as "the process is
    ///     dead" is the exact defect that branch was carved out to avoid.</item>
    ///   <item><c>DIVERGENT</c> — both legs failed but in DIFFERENT frames, which the verdict
    ///     already refuses to call one process-wide fault.</item>
    /// </list>
    ///
    /// <para>🚨 And it is <b>not</b> "the exception carries a canary at all". Every emit-phase
    /// throw carries one; only two of the five verdicts say the process is the broken thing.
    /// Keying on presence would widen the non-verdict to every infrastructure fault — the blind
    /// spot <c>SourceSnapshotEstablishmentTest.EveryOtherCompileFailure_StillStampsError</c>
    /// exists to refuse — so the verdict has to be READ, not merely found.</para>
    ///
    /// <para>Pure and total: any other string, and <c>null</c>, answer false. The parameter is
    /// <see cref="object"/> because it is read straight out of
    /// <see cref="System.Collections.IDictionary"/> <c>Exception.Data</c>, where a value of the
    /// wrong type is a real possibility and must degrade to "not proven".</para>
    /// </summary>
    /// <param name="canaryVerdict">The value stamped under <see cref="EmitCanaryDataKey"/>.</param>
    internal static bool IsProcessEmitFailure(object? canaryVerdict) =>
        canaryVerdict is string verdict
        && (verdict.StartsWith("canary=BELOW-ROSLYN", StringComparison.Ordinal)
            || verdict.StartsWith("canary=REFERENCES", StringComparison.Ordinal));

    /// <summary>
    /// A minimal, self-contained compilation used ONLY by <see cref="ProbeSharedEmitState"/>.
    /// Three levels of nested generics on purpose: that is what makes Roslyn's metadata writer
    /// walk a type's containing chain (<c>GetConsolidatedTypeParameters</c> recursing through
    /// <c>ContainingTypeDefinition</c>) — the exact path issue #890's NRE dies on. A flat class
    /// would emit fine even on a poisoned writer and the canary would answer "healthy" wrongly.
    /// </summary>
    private const string EmitCanarySource =
        "public class MwEmitCanary<T> { public class Inner<U> { public class Leaf<V> "
        + "{ public T A; public U B; public V C; } } }";

    /// <summary>
    /// Answers, at the moment a Roslyn <c>Emit</c> throws, which state is actually broken — in
    /// TWO legs, because the first leg alone cannot tell "the shared reference set is poisoned"
    /// from "the process is broken below Roslyn".
    ///
    /// <para><b>Leg 1 — same references.</b> Re-emit a trivial nested-generic compilation built
    /// against <b>the same <see cref="MetadataReference"/> instances</b> as the compilation that
    /// just failed. Succeeds ⇒ shared state is healthy and the fault is a property of this node's
    /// own compilation inputs (dump its generated source). This is the leg #1378 shipped, and on
    /// 2026-08-13 it returned THREW — closing the "generated source" half of the search
    /// space.</para>
    ///
    /// <para><b>Leg 2 — pristine references.</b> Run only when leg 1 fails: the SAME source
    /// against a freshly created, minimal reference set that has never been handed to Roslyn
    /// before and is shared with nothing — see the mapping note below for what "nothing" had to
    /// be widened to mean. This is the discriminator, and it exists because
    /// Roslyn's own source settles who can supply the null. The NRE's guard
    /// (<c>NamedTypeSymbolAdapter.AsNestedTypeDefinitionImpl</c>) admits a type only when
    /// <c>ContainingModule == moduleBeingBuilt.SourceModule</c> — so the symbol whose
    /// <c>ContainingType</c> reads null is a <b>source</b> symbol of the compilation being
    /// emitted, never a PE symbol arriving from a reference. Leg 1 therefore proves the fault is
    /// process-wide without proving the reference set carries it.
    /// <list type="bullet">
    ///   <item><c>canary=REFERENCES</c> — pristine emits, shared does not: the poison travels
    ///     with the reference instances (or the symbols Roslyn caches on their
    ///     <c>AssemblyMetadata</c>), and scoping the set per-mesh is on the right axis.</item>
    ///   <item><c>canary=DIVERGENT</c> — neither emits, but they died in DIFFERENT frames. The
    ///     two legs run identical source, so two different faults are not evidence of one
    ///     process-wide fault; both sites are named and the below-Roslyn claim is withheld.</item>
    ///   <item><c>canary=BELOW-ROSLYN</c> — neither emits, IN THE SAME FRAME: freshly parsed source
    ///     and an IMAGE-BACKED CoreLib (sharing neither the reference instances nor their file
    ///     mappings) still cannot emit, so nothing about the reference set explains it. The
    ///     broken state is under Roslyn (CLR heap / JIT / GC) and no reference-set change can fix
    ///     it. Roslyn keeps no cross-emit state — the metadata writer's indices are per-emit, its
    ///     object pools hold only scratch buffers, and <c>AssemblyMetadata.CachedSymbols</c> is a
    ///     weak list of assembly symbols — so there is no Roslyn cache left to blame.</item>
    ///   <item><c>canary=INCONCLUSIVE</c> — the pristine control could not be BUILT (no on-disk
    ///     CoreLib to reference), so leg 2 never ran. Reported as its own verdict rather than
    ///     folded into BELOW-ROSLYN: a probe that answers its scariest branch on its own
    ///     inability would send triage after a CLR heap bug nothing observed.</item>
    /// </list></para>
    ///
    /// <para>🚨 <b>The control must not share the one file both sets must map.</b> Leg 2 built its
    /// CoreLib with <c>MetadataReference.CreateFromFile(typeof(object).Assembly.Location)</c> —
    /// and <see cref="CompileReferences"/> maps <b>that same path</b>, both from
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> and again as an explicit
    /// <c>typeof(object).Assembly</c> addition. Distinct <c>PortableExecutableReference</c>
    /// instances, yes — but the same on-disk image, hence the same mmap and the same OS page-cache
    /// pages. Every compilation must reference CoreLib, so that overlap is unavoidable *by
    /// construction*: the "pristine" leg shared with the poisoned set precisely the one input no
    /// compile can omit. A fault in those mapped metadata pages (a torn or evicted page on the
    /// runner's overlayfs, a bad mapping) therefore killed BOTH legs in the SAME frame and was
    /// reported as <c>BELOW-ROSLYN</c> — *"nothing about the reference set explains this … capture
    /// a core dump"* — which is the most expensive answer this probe can give, handed out on
    /// evidence that never excluded the mapping. Nine CI occurrences (2026-08-23 → 08-28) all
    /// returned that verdict unanimously, and it steered #890's triage away from the reference set
    /// on a distinction the probe had not actually drawn. Leg 2 now uses
    /// <c>CreateFromImage(File.ReadAllBytes(...))</c>: fresh managed bytes, no mapping shared with
    /// anything. This is the same defect as the message-vs-site one below, one layer down — a
    /// control is only a control for what it does not share.</para>
    ///
    /// <para>🚨 It cannot fail into the fault path it is diagnosing: every outcome — including
    /// the canary throwing — returns a STRING. A diagnostic that throws while diagnosing would
    /// replace the original exception and destroy the evidence it exists to preserve. It is also
    /// bounded: one tiny in-memory emit, only ever on an already-failing path, never on the
    /// success path or on a normal compile error.</para>
    /// </summary>
    /// <param name="faulted">The compilation whose <c>Emit</c> threw; only its references are used.</param>
    /// <returns>A one-line verdict, safe to append to a log message.</returns>
    internal static string ProbeSharedEmitState(CSharpCompilation faulted)
    {
        var shared = EmitCanary(() => faulted.References);
        if (shared.StartsWith("OK", StringComparison.Ordinal))
            return "canary=OK (a trivial nested-generic emit against the SAME reference set "
                + "still succeeds ⇒ shared Roslyn/reference state is healthy; the fault is "
                + "specific to THIS compilation's inputs — dump its generated source)";

        // The shared-reference canary failed. Re-run the SAME source against a reference set that
        // shares NOTHING with this process's other compilations — freshly created, minimal
        // (System.Private.CoreLib is all the canary source needs), and never handed to Roslyn
        // before.
        //
        // 🚨 BUILDING the pristine reference is a SEPARATE step from emitting against it, and its
        // failure is a SEPARATE verdict. If CreateFromFile cannot produce it (an empty
        // Assembly.Location on a single-file host, a missing file), the discriminator was never
        // run — and folding that into BELOW-ROSLYN would make the probe answer its scariest
        // branch on its own inability, sending triage after a CLR heap bug that nothing observed.
        // A diagnostic that cannot report "I could not run" is the same defect as a gate that
        // passes when its input is missing.
        var pristineRefs = TryBuildPristineControl(out var pristineUnavailable);

        if (pristineUnavailable is not null)
            return $"canary=INCONCLUSIVE shared:{shared} pristine:UNAVAILABLE({pristineUnavailable}) "
                + "— the shared reference set cannot emit, but the pristine control could not be "
                + "BUILT, so this says nothing about whether the reference set is the cause";

        return Verdict(shared, EmitCanary(() => pristineRefs));
    }

    /// <summary>
    /// Test seam: run ONE canary leg against a given reference set and return its outcome token.
    /// Lets <c>EmitCanaryControlTest</c> assert that the control can still emit — a control that
    /// cannot compile would retire the discriminator silently, turning every occurrence into
    /// INCONCLUSIVE with nothing going red.
    /// </summary>
    internal static string EmitCanaryForTest(IReadOnlyList<MetadataReference> references)
        => EmitCanary(() => references);

    /// <summary>
    /// Builds leg 2's control reference set: CoreLib, and nothing else.
    ///
    /// <para>🚨 <b>Image-backed, never file-backed</b> — the whole point of the control is that it
    /// shares nothing with the reference set under suspicion, and
    /// <c>CreateFromFile(typeof(object).Assembly.Location)</c> shared the single file
    /// <see cref="CompileReferences.Default"/> is guaranteed to map. Reading the bytes and using
    /// <c>CreateFromImage</c> gives a reference backed by a fresh managed array: no mmap, no
    /// shared page-cache pages, no shared <c>AssemblyMetadata</c>. Extracted so the invariant is
    /// assertable — <c>EmitCanaryControlTest</c> pins that the control carries no
    /// <c>FilePath</c> while the shared set does map that same file, which is exactly the overlap
    /// that made <c>BELOW-ROSLYN</c> unearned.</para>
    ///
    /// <para>Never throws: a failure to build the control is a SEPARATE verdict
    /// (<c>INCONCLUSIVE</c>), never folded into <c>BELOW-ROSLYN</c>. Cost is one ~15 MB read, only
    /// ever on an already-failing path.</para>
    /// </summary>
    /// <param name="unavailable">Why the control could not be built, or <c>null</c> on success.</param>
    internal static IReadOnlyList<MetadataReference> TryBuildPristineControl(out string? unavailable)
    {
        unavailable = null;
        try
        {
            var coreLib = typeof(object).Assembly.Location;
            if (string.IsNullOrEmpty(coreLib) || !File.Exists(coreLib))
            {
                unavailable = "no on-disk System.Private.CoreLib to reference";
                return [];
            }

            return [MetadataReference.CreateFromImage(File.ReadAllBytes(coreLib))];
        }
        catch (Exception buildError)
        {
            unavailable = $"{buildError.GetType().Name}: {buildError.Message}";
            return [];
        }
    }

    /// <summary>
    /// Reduces the two canary legs to the one-line verdict. Pure — no Roslyn, no process state —
    /// so every branch is unit-testable (<c>EmitCanaryVerdictTest</c>).
    ///
    /// <para>🚨 <b><c>BELOW-ROSLYN</c> requires the two legs to have died in the SAME frame</b>,
    /// not merely to have both failed. Both legs run the SAME source; the only difference is the
    /// reference set. So "shared threw and pristine threw" is the evidence for a process-wide
    /// fault only when it is the SAME fault — and until the throw site was recorded
    /// (<see cref="ThrowSite"/>) nothing checked that. Every <see cref="NullReferenceException"/>
    /// in .NET carries the identical message, so an unrelated NRE in the pristine leg (a
    /// reference that could not be read, an OOM surfacing as a null, a probe-side bug) read as a
    /// confirmation and the probe answered its most expensive branch — *"the broken state is
    /// below Roslyn (CLR heap / JIT / GC) … capture a core dump"* — on a coincidence of wording.
    /// When the sites differ the verdict is <c>DIVERGENT</c>: both sites are named and the strong
    /// claim is withheld, exactly as <c>INCONCLUSIVE</c> withholds it when the control never
    /// ran.</para>
    /// </summary>
    /// <param name="shared">Leg 1's outcome token — the same reference set as the failed compile.</param>
    /// <param name="pristine">Leg 2's outcome token — brand-new references.</param>
    internal static string Verdict(string shared, string pristine)
    {
        if (pristine.StartsWith("OK", StringComparison.Ordinal))
            return $"canary=REFERENCES shared:{shared} pristine:{pristine} — the same source emits fine "
                + "against an IMAGE-BACKED CoreLib but fails against the shared set ⇒ the poison "
                + "travels with the shared reference state: either the MetadataReference instances "
                + "and the Roslyn symbols cached on them, or the mmap'd on-disk images they map "
                + "(the control shares neither). Scope the reference set per mesh to separate the "
                + "two — and if the shared set's CoreLib is implicated, suspect the file mapping, "
                + "not the instance";

        var sharedSite = SiteOf(shared);
        var pristineSite = SiteOf(pristine);
        if (sharedSite is null || pristineSite is null
            || !string.Equals(sharedSite, pristineSite, StringComparison.Ordinal))
            return $"canary=DIVERGENT shared:{shared} pristine:{pristine} — both legs failed, but "
                + "NOT in the same way (shared died at "
                + $"'{sharedSite ?? "an unrecorded site"}', pristine at '{pristineSite ?? "an unrecorded site"}'), "
                + "and the two legs run identical source. Two different faults are not evidence of "
                + "one process-wide fault, so the below-Roslyn verdict is withheld — COMPARE THE "
                + "TWO SITES: one corruption can surface a frame apart, but two unrelated faults "
                + "look exactly like this as well, and only the sites tell them apart. Start with "
                + "whichever site is not the emit itself";

        return $"canary=BELOW-ROSLYN shared:{shared} pristine:{pristine} — a trivial compilation "
            + "with freshly parsed source and an IMAGE-BACKED CoreLib (fresh managed bytes, "
            + "sharing neither the MetadataReference instances nor the mmap'd images of the "
            + $"shared set) cannot emit either, and BOTH legs died in the same frame ({sharedSite}) "
            + "⇒ nothing about the reference set OR its file mappings explains this; the "
            + "broken state is below Roslyn (CLR heap / JIT / GC), so no reference-set change can "
            + "fix it — capture a core dump and re-run with tiering disabled. RESIDUAL: both legs "
            + "still run on the one CLR, so this does not separate a corrupted heap from a "
            + "miscompiled Roslyn method — compare the dump's faulting address against #613";
    }

    /// <summary>
    /// The <c>Type.Method</c> frame out of a leg token shaped
    /// <c>THREW {Type} at {Site}: {Message}</c>, or <c>null</c> when the leg did not throw or
    /// recorded no site (a <c>DIAGNOSTICS(...)</c> outcome, or a token from an older shape).
    /// </summary>
    private static string? SiteOf(string leg)
    {
        const string marker = " at ";
        if (!leg.StartsWith("THREW ", StringComparison.Ordinal))
            return null;
        var at = leg.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var colon = leg.IndexOf(':', at);
        var site = (colon < 0 ? leg[(at + marker.Length)..] : leg[(at + marker.Length)..colon]).Trim();
        return string.IsNullOrEmpty(site) || site == "(no stack)" ? null : site;
    }

    /// <summary>
    /// One canary leg: emit <see cref="EmitCanarySource"/> against the references
    /// <paramref name="references"/> produces, into memory, and reduce the outcome to a short
    /// token. Never throws — see the "cannot fail into the fault path it is diagnosing" note on
    /// <see cref="ProbeSharedEmitState"/>. The references arrive as a FACTORY so that building
    /// them is inside this method's try as well: on a poisoned process even
    /// <c>MetadataReference.CreateFromFile</c> is a candidate to fail.
    /// </summary>
    private static string EmitCanary(Func<IEnumerable<MetadataReference>> references)
    {
        try
        {
            var canary = CSharpCompilation.Create(
                "MeshWeaverEmitCanary",
                syntaxTrees: [CSharpSyntaxTree.ParseText(EmitCanarySource)],
                references: references(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var canaryStream = new MemoryStream();
            var result = canary.Emit(canaryStream);
            if (result.Success)
                return "OK";

            var ids = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id).Distinct().Take(5);
            return $"DIAGNOSTICS({string.Join(",", ids)})";
        }
        catch (Exception probeError)
        {
            return $"THREW {probeError.GetType().Name} at {ThrowSite(probeError)}: {probeError.Message}";
        }
    }

    /// <summary>
    /// The frame an exception was thrown FROM, as <c>Type.Method</c> — the discriminator the
    /// canary's verdict is decided on.
    ///
    /// <para>🚨 Why the verdict cannot be decided on the message. Both legs reduced their outcome
    /// to <c>"THREW {Type}: {Message}"</c>, and <c>BELOW-ROSLYN</c> was claimed whenever the
    /// pristine leg <i>also threw anything at all</i>. "Object reference not set to an instance of
    /// an object." is the same string for every <see cref="NullReferenceException"/> in .NET, so
    /// two throws from completely different code read as the same fault — and the verdict that
    /// follows ("the broken state is below Roslyn (CLR heap / JIT / GC) … capture a core dump")
    /// is the most expensive one this probe can hand triage. That is the same defect the
    /// <c>INCONCLUSIVE</c> branch exists to avoid, one step further in: a probe must not answer
    /// its scariest branch on evidence it never checked. With the site recorded, "both legs died
    /// in the SAME frame" is a fact rather than an inference.</para>
    ///
    /// <para>Never throws and never returns null — it runs on an already-failing path (see the
    /// "cannot fail into the fault path it is diagnosing" note on
    /// <see cref="ProbeSharedEmitState"/>), so an unavailable stack degrades to
    /// <c>(no stack)</c>.</para>
    /// </summary>
    internal static string ThrowSite(Exception error)
    {
        try
        {
            // TargetSite is the throwing method itself and survives a stack trace the runtime
            // could not materialize; the first stack frame is the fallback for the rare
            // TargetSite-less throw.
            var method = error.TargetSite;
            if (method is not null)
                return $"{method.DeclaringType?.Name ?? "?"}.{method.Name}";
            var firstFrame = error.StackTrace?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();
            return string.IsNullOrEmpty(firstFrame) ? "(no stack)" : firstFrame;
        }
        catch
        {
            return "(no stack)";
        }
    }

    /// <summary>
    /// Number of times <see cref="EmitToDiskWithRetry"/> re-emits when a "successful" Roslyn
    /// emit leaves no assembly on disk (ephemeral-cache eviction). Three attempts recover a
    /// transient lost write while still failing fast on a genuinely unwritable cache directory.
    ///
    /// <para>Why a retry is legitimate here (and is NOT covering for a defect of ours): the
    /// condition is genuinely EXTERNAL and transient — a container runtime reclaiming an
    /// ephemeral <c>/tmp</c> under memory pressure between our write and our read. Nothing in
    /// this process can prevent it, there is no lock/slot/budget being leaked, and the retry is
    /// bounded, stateless and timer-free: three synchronous attempts, then a loud terminal
    /// failure. A deterministic compile error is explicitly NOT retried. Its counterfactual is a
    /// permanently poisoned NodeType (prod AgenticPension/Datenpunkt, 2026-06-22), not a slower
    /// recovery. Evidence that it is not masking anything: across every production portal it
    /// has fired ZERO times in 7 days (31M log lines) — every ERROR the compile service emits in
    /// production is a genuine Roslyn diagnostic, never a lost write.</para>
    /// </summary>
    internal const int DiskEmitAttempts = 3;

    /// <summary>
    /// Emits to a fresh per-attempt subdirectory under <paramref name="cacheDirectory"/> and
    /// confirms the assembly on disk IS the image that was emitted, re-emitting up to
    /// <paramref name="maxAttempts"/> times when it is not. <paramref name="emitToReleaseDir"/> runs
    /// the real Roslyn emit into the supplied directory and returns the DLL path together with the
    /// digest of the image it produced (<see cref="EmittedArtifact"/>); it may throw
    /// <see cref="CompilationException"/> for a genuine compile error, which propagates immediately
    /// (NEVER retried — only a lost or mismatched artifact triggers a re-emit). <c>internal</c> so
    /// the publication contract is unit-testable without a real flaky filesystem.
    /// </summary>
    internal static string EmitToDiskWithRetry(
        string cacheDirectory,
        string nodeName,
        int maxAttempts,
        ILogger logger,
        Func<string, EmittedArtifact> emitToReleaseDir)
    {
        string? lastDllPath = null;
        // Why the LAST attempt failed to publish, carried into the terminal exception: "could not
        // be persisted" alone sent operators looking for a read-only cache directory when the real
        // answer was an artifact that is present and unreadable.
        var lastReason = "the artifact never appeared";

        static void TryDeleteDir(string dir)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var timestamp = DateTimeOffset.UtcNow.Ticks.ToString("x");
            // Unique published name. Discovery orders by the dir's LastWriteTime, NOT by parsing the
            // ticks out of the name (see TryGetLatestCachedDllPath), so a GUID suffix guarantees the
            // atomic Directory.Move below never collides — on a coarse clock or two rapid compiles —
            // while still matching the `{nodeName}_*` glob.
            var releaseDir = Path.Combine(cacheDirectory, $"{nodeName}_{timestamp}_{Guid.NewGuid():N}");
            lastDllPath = Path.Combine(releaseDir, $"{nodeName}.dll");

            // 🚨 Emit into a STAGING dir whose name does NOT match the `{nodeName}_*` discovery glob
            // (TryGetLatestCachedDllPath), then atomically publish it by renaming to the discoverable
            // name only AFTER the DLL is fully written + verified. The DLL file exists at 0 bytes and
            // grows during compilation.Emit (File.Create + Emit is NOT atomic); without staging, a
            // concurrent reader can discover the half-written DLL and LoadFromAssemblyPath a truncated
            // image → a native crash (SIGSEGV) or a BadImageFormat that deletes the artifact and churns
            // the compile. A directory rename on the same filesystem is atomic, so a reader sees either
            // nothing or the COMPLETE artifact.
            var stagingDir = Path.Combine(cacheDirectory, $".staging-{nodeName}-{timestamp}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            // The real emit (a genuine compile error throws straight through — never retried). Discard
            // the half-written staging dir first so a failed emit leaves no partial artifact behind (the
            // old code leaked a glob-discoverable `{nodeName}_{ticks}` dir here — the same hazard).
            EmittedArtifact staged;
            try
            {
                staged = emitToReleaseDir(stagingDir);
            }
            catch
            {
                TryDeleteDir(stagingDir);
                throw;
            }

            // Confirm the staged file IS the image the emit produced, then atomically publish. EVERY
            // fault here is a RETRYABLE publish failure — an ephemeral-cache eviction racing the
            // read, a lost or partial write, or a transient rename IO error — so discard staging and
            // re-emit rather than aborting the compile.
            //
            // 🚨 The predicate is "these are the bytes we emitted", NOT "this file is non-empty".
            // `Length > 0` accepts a 1-byte file, a truncated PE, and — the case that survived
            // #1387 — a full-length image with an unwritten region inside its metadata. None of
            // those is rejected by the loader in any way the pipeline survives: the first two make
            // LoadNodeAssembly return null, the third loads fine and throws
            // ReflectionTypeLoadException "…because the format is invalid" on the first GetTypes().
            // CompileResultFromAssembly records either as CompilationStatus.Error, and the
            // first-build kickoff is gated on Status == null, so it NEVER retries — the bytes may
            // heal, the verdict does not, and the NodeType is parked for good (#1412). Proving the
            // artifact before it enters the discovery namespace is the publication contract, the
            // same one AtomicFileWrite gives the assembly store; it is emphatically NOT a retry
            // around a load. See EmittedArtifact for why a digest and not a metadata walk.
            try
            {
                if (staged.MatchesFileOnDisk(out lastReason))
                {
                    Directory.Move(stagingDir, releaseDir);
                    return lastDllPath;
                }

                logger.LogWarning(
                    "Emit for {NodeName} reported success but the staged assembly at {DllPath} is " +
                    "not the image that was emitted — {Reason} (attempt {Attempt}/{Max}); re-emitting.",
                    nodeName, staged.DllPath, lastReason, attempt, maxAttempts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastReason = $"publishing failed — {ex.GetType().Name}: {ex.Message}";
                logger.LogWarning(ex,
                    "Publishing the emitted assembly for {NodeName} failed (attempt {Attempt}/{Max}); re-emitting.",
                    nodeName, attempt, maxAttempts);
            }

            // Drop the staging directory so the retry starts clean.
            TryDeleteDir(stagingDir);
        }

        throw new CompilationException(nodeName,
            $"Compilation succeeded but the emitted assembly for '{nodeName}' could not be published to " +
            $"'{cacheDirectory}' after {maxAttempts} attempts (last target '{lastDllPath}'; last failure: " +
            $"{lastReason}). The compilation host's cache directory may be read-only, evicting files, or " +
            "losing writes.");
    }

    /// <summary>
    /// Compiles and emits the assembly to memory (no disk I/O), returning the DLL + PDB bytes for
    /// the caller to load. Like <see cref="EmitCompilationToDirectory"/>, a failed emit throws
    /// UNLOGGED — the pipeline's single <c>.Catch&lt;…, CompilationException&gt;</c> funnel is the
    /// one reporter of a compile failure.
    /// </summary>
    internal static (byte[] AssemblyBytes, byte[] PdbBytes) EmitToMemory(
        CSharpCompilation compilation, string nodePath, CancellationToken ct)
    {
        using var dllStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb);

        var emitResult = compilation.Emit(dllStream, pdbStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
            throw new CompilationException(nodePath,
                CompileDiagnostics.FormatCompileFailure(nodePath, emitResult.Diagnostics));

        return (dllStream.ToArray(), pdbStream.ToArray());
    }
}
