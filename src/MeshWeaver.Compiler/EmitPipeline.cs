using System.Reflection;
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
        => EmitCompilationToDirectory(compilation, nodeName, nodePath, releaseDir, [], ct);

    /// <summary>
    /// The same verified emit, carrying MANAGED RESOURCES into the assembly — the shape
    /// <c>mw-plugin-test build-project</c> needs to reproduce an SDK build of a project with
    /// <c>&lt;EmbeddedResource&gt;</c> items.
    ///
    /// <para>🚨 <b>A separate overload rather than an optional parameter on the one above.</b>
    /// Adding a parameter — even a defaulted one — REPLACES a method's signature, so an assembly
    /// compiled against the old arity calls a method the new one does not have; that is the same
    /// binary break <c>scripts/check-record-signatures.py</c> exists to refuse for records, and it
    /// applies here for exactly the same reason. The overload leaves the four-argument entry point
    /// byte-identical for <c>MeshNodeCompilationService</c> and <c>NodeSetCompiler</c>, which pass
    /// no resources and never will: a dynamic NodeType is generated source, not a project.</para>
    ///
    /// <para>Nothing else differs. The emit is still to MEMORY first and the bytes written out, so
    /// <see cref="EmittedArtifact"/> can prove the file on disk is the image that was emitted
    /// (#1412), and this still does not log (the log-once contract).</para>
    /// </summary>
    /// <param name="compilation">The compilation to emit.</param>
    /// <param name="nodeName">Base name for the emitted files.</param>
    /// <param name="nodePath">Path reported in a <see cref="CompilationException"/>.</param>
    /// <param name="releaseDir">Directory to write into.</param>
    /// <param name="manifestResources">Managed resources to embed; empty for a dynamic NodeType.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The artifact descriptor, for the publisher to verify.</returns>
    internal static EmittedArtifact EmitCompilationToDirectory(
        CSharpCompilation compilation, string nodeName, string nodePath, string releaseDir,
        IReadOnlyCollection<ResourceDescription> manifestResources, CancellationToken ct)
        => EmitCompilationToDirectory(compilation, nodeName, nodePath, releaseDir,
            manifestResources, ct, null);

    // The existing entry points keep their signatures and default behavior. Only the
    // NodeType pipeline supplies the optional CI diagnostic scheduler; it must not do
    // blocking I/O here or replace the exception that caused the capture.
    internal static EmittedArtifact EmitCompilationToDirectory(
        CSharpCompilation compilation, string nodeName, string nodePath, string releaseDir,
        IReadOnlyCollection<ResourceDescription> manifestResources, CancellationToken ct,
        Action<CSharpCompilation, Exception>? captureFailure)
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
                manifestResources: manifestResources.Count == 0 ? null : manifestResources,
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
            try
            {
                captureFailure?.Invoke(compilation, ex);
            }
            catch (Exception captureError)
            {
                // Scheduling/ownership can fail during teardown. Diagnostic failure
                // must never wrap, replace or suppress the original Roslyn exception.
                ex.Data[EmitReferenceCaptureDataKey] = "incomplete:scheduling-" + captureError.GetType().Name;
            }
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
    /// Key under which <see cref="EmitCompilationToDirectory(CSharpCompilation, string, string, string, CancellationToken)"/> stamps the canary verdict on a
    /// thrown-from-Emit exception, and under which
    /// <c>NodeTypeCompilationHelpers.SummarizeCompileError</c> reads it back.
    /// </summary>
    internal const string EmitCanaryDataKey = "MeshWeaver.EmitCanary";

    internal const string EmitReferenceCaptureDataKey = "MeshWeaver.EmitReferenceCapture";

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
    /// <c>ContainingTypeDefinition</c>) — the exact path issue #890's NRE dies on.
    ///
    /// <para>🚨 This comment used to end *"a flat class would emit fine even on a poisoned writer
    /// and the canary would answer 'healthy' wrongly"*. That was an assumption, never a
    /// measurement, and it is load-bearing in both directions: it is the reason the canary uses a
    /// nested source, and it is the reason every occurrence has been read as "this process cannot
    /// emit AT ALL". <see cref="FlatCanarySource"/> and the <c>flat=</c> leg
    /// (<see cref="ClassifyFlatLeg"/>) turn it into a reading.</para>
    /// </summary>
    private const string EmitCanarySource =
        "public class MwEmitCanary<T> { public class Inner<U> { public class Leaf<V> "
        + "{ public T A; public U B; public V C; } } }";

    /// <summary>
    /// The SAME probe with the ONE variable this defect turns on removed: a single top-level,
    /// non-generic, member-less class. Deliberately the narrowest source that still makes
    /// Roslyn's metadata writer ask the #890 question and no other.
    ///
    /// <para><b>Why it discriminates.</b> <c>MetadataWriter.GetConsolidatedTypeParameters</c>
    /// opens with <c>typeDef.AsNestedTypeDefinition(Context)</c> and returns IMMEDIATELY when that
    /// answers null — the recursive overload, and with it the
    /// <c>ITypeDefinitionMember.ContainingTypeDefinition</c> call every #890 stack dies in, is
    /// never reached for a top-level type. The class carries no members either, so the
    /// <c>NamedTypeSymbol</c> overload of that property has exactly one possible caller left:
    /// <c>AsNestedTypeDefinitionImpl</c>'s guard having answered TRUE for a type whose containing
    /// type is null by construction.</para>
    ///
    /// <para>So the two outcomes say different things, and neither was previously observable:
    /// <list type="bullet">
    ///   <item><b>It emits</b> ⇒ the process is NOT emit-dead; the fault needs the nested /
    ///     generic walk, the guard is intact, and "every later compile in this process will fail
    ///     the same way" is true of the workload but not of emit as such.</item>
    ///   <item><b>It dies in the same frame</b> ⇒ the writer reached that frame with no nested
    ///     type anywhere in the compilation, i.e. the guard read TRUE where it must read FALSE.
    ///     That is #890 in ONE method, with no recursion, no generics and no nesting — the
    ///     smallest form this defect could take, and what a <c>dotnet/runtime</c> report needs.</item>
    /// </list></para>
    /// </summary>
    internal const string FlatCanarySource = "public class MwFlatEmitCanary { }";

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

        // Legs 3 and 4 run only on the two verdicts that mean "the control could not emit either",
        // and both are passed as FACTORIES so Verdict stays pure and unit-testable.
        //
        // 🚨 Leg 4 uses the FAULTED compilation's OWN references — the same set leg 1 used — on
        // purpose. Leg 2 varies the reference set; leg 4 varies the SOURCE and nothing else, so
        // "the flat source emits and the nested one does not" is a statement about nesting rather
        // than about references. Running it against the pristine set would confound the two.
        return Verdict(
            shared,
            EmitCanary(() => pristineRefs),
            () => DissectTheNull(() => faulted.References),
            () => EmitCanary(() => faulted.References, FlatCanarySource));
    }

    /// <summary>
    /// Test seam: run ONE canary leg against a given reference set and return its outcome token.
    /// Lets <c>EmitCanaryControlTest</c> assert that the control can still emit — a control that
    /// cannot compile would retire the discriminator silently, turning every occurrence into
    /// INCONCLUSIVE with nothing going red.
    /// </summary>
    internal static string EmitCanaryForTest(
        IReadOnlyList<MetadataReference> references, string source = EmitCanarySource)
        => EmitCanary(() => references, source);

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

    /// <summary>The internal Roslyn symbol behind a public <c>ISymbol</c> wrapper. Reached by
    /// reflection because Roslyn exposes no public route to it — <c>EmitCanaryDissectionTest</c>
    /// is what stops a Roslyn rename from retiring leg 3 in silence.</summary>
    private const string UnderlyingSymbolProperty = "UnderlyingSymbol";

    /// <summary>The interface whose explicit implementation on <c>NamedTypeSymbol</c> is the exact
    /// frame every #890 stack dies in.</summary>
    private const string CciTypeDefinitionMember = "Microsoft.Cci.ITypeDefinitionMember";

    /// <summary>The getter on <see cref="CciTypeDefinitionMember"/> that reads null.</summary>
    private const string ContainingTypeDefinitionGetter = "get_ContainingTypeDefinition";

    /// <summary>
    /// Leg 3 — DISSECT the null, instead of asking for a core dump nobody can produce.
    ///
    /// <para>Every #890 stack dies in the same two frames:
    /// <c>NamedTypeSymbol.Microsoft.Cci.ITypeDefinitionMember.get_ContainingTypeDefinition()</c>,
    /// which in a Release Roslyn reduces to <c>return this.ContainingType.GetCciAdapter();</c>,
    /// called from <c>MetadataWriter.GetConsolidatedTypeParameters</c> — whose guard
    /// (<c>AsNestedTypeDefinitionImpl</c>) read that same <c>ContainingType</c> as NON-null
    /// microseconds earlier. Legs 1 and 2 establish that the process can no longer EMIT. Neither
    /// asks the far cheaper question: <b>can it still perform the READ that the emit dies on?</b>
    /// This leg does, on symbols bound after the fault, and its answer is the discriminator the
    /// <c>BELOW-ROSLYN</c> verdict itself flags as residual.</para>
    ///
    /// <list type="bullet">
    ///   <item><c>dissect=SYMBOL-GRAPH-BROKEN</c> — the ordinary <c>ContainingType</c> property
    ///     reads null for a nested SOURCE type of a compilation created AFTER the fault. The
    ///     broken read is then the property itself, not anything about emit, and every consumer of
    ///     a nested symbol in this process is affected — not only <c>Emit</c>.</item>
    ///   <item><c>dissect=REPRODUCED-OUTSIDE-EMIT</c> — the public property reads correctly but the
    ///     Cci explicit interface implementation does not, called DIRECTLY. #890 then reproduces in
    ///     one property call with no emit, no metadata writer and no PE stream: the smallest repro
    ///     this defect has ever had, and the one a <c>dotnet/runtime</c> report needs.</item>
    ///   <item><c>dissect=GUARD-READ-BROKEN</c> — the TOP-LEVEL type's <c>ContainingType</c> reads
    ///     NON-null when it is null by construction. That is the read
    ///     <c>AsNestedTypeDefinitionImpl</c>'s guard makes, and a wrong TRUE there is on its own
    ///     sufficient to produce #890 — see the polarity note below.</item>
    ///   <item><c>dissect=READS-HEALTHY</c> — ALL THREE reads return the right answer, on freshly
    ///     bound symbols, microseconds after <c>Emit</c> threw on exactly this shape. Neither a
    ///     corrupted object graph nor a broken guard read predicts that; code that is only wrong
    ///     when reached from <c>MetadataWriter</c>'s own call site does — which is what the
    ///     split-arm <c>DOTNET_TieredPGO=0</c> re-run tests.</item>
    ///   <item><c>dissect=UNAVAILABLE(…)</c> — the probe could not run (a Roslyn shape change, an
    ///     unbindable canary). Its OWN verdict, never folded into the others: the
    ///     <c>INCONCLUSIVE</c> lesson one layer further in.</item>
    /// </list>
    ///
    /// <para>🚨 <b>BOTH POLARITIES, because the read that fails is a NULL that must stay null.</b>
    /// This leg shipped reading only <c>Leaf.ContainingType</c> and <c>Inner.ContainingType</c> —
    /// two reads that must come back NON-null — and the first two readings it ever produced
    /// (2026-09-05 and 2026-09-06, both <c>READS-HEALTHY</c>) were earned on that half alone. But
    /// look at what the metadata writer actually does:
    /// <code>
    /// // AsNestedTypeDefinitionImpl — the GUARD
    /// if ((object)ContainingType != null &amp;&amp; IsDefinition &amp;&amp; ContainingModule == …) return this;
    /// // ITypeDefinitionMember.ContainingTypeDefinition — reached ONLY when the guard said TRUE
    /// return ContainingType.GetCciAdapter();          // NRE iff ContainingType is null
    /// </code>
    /// <c>getConsolidatedTypeParameters</c> recurses up the containing chain and calls these two
    /// back to back on the same object, so it walks Leaf → Inner → <c>MwEmitCanary</c> — and at the
    /// TOP-LEVEL type the guard is supposed to answer FALSE and stop the recursion. If that read
    /// answers TRUE, the property is then called on a type whose <c>ContainingType</c> is null
    /// <i>correctly</i>, and it throws the #890 NRE at the #890 frame. <b>No corrupted symbol and
    /// no pair of disagreeing reads is required for that</b> — which is the framing this issue has
    /// carried since 2026-08-13. A probe that only ever asks "does a non-null read come back
    /// non-null" cannot see it, and would report a process broken in exactly that way as
    /// <c>symbol:OK</c>. So the top-level read is made too, and it must come back NULL.</para>
    ///
    /// <para>🚨 The Cci leg stays on the NESTED type on purpose. On a perfectly healthy process,
    /// <c>get_ContainingTypeDefinition</c> called on a TOP-LEVEL type throws a
    /// <see cref="NullReferenceException"/> at the exact frame every #890 stack names — correctly,
    /// because the containing type really is null. Probing it there would manufacture this
    /// defect's signature on every healthy run. The frame is not the finding; the metadata writer
    /// having REACHED it is.</para>
    ///
    /// <para>🚨 A local control (our own nested generic + explicit interface implementation) was
    /// considered and deliberately left out. Its failing branch would be informative, but its
    /// passing branch is not — a different jitted method reading correctly says nothing about
    /// Roslyn's — and a probe whose green means nothing is exactly the shape this file keeps
    /// removing.</para>
    ///
    /// <para>Never throws, like every other leg. Cost is one parse and two property reads, only
    /// ever on a path that has already failed.</para>
    /// </summary>
    /// <param name="references">The reference set to bind the canary source against — the FAULTED
    /// compilation's own, so the read runs under the conditions the failed emit ran under.</param>
    internal static string DissectTheNull(Func<IEnumerable<MetadataReference>> references)
    {
        INamedTypeSymbol? leaf;
        string symbolLeg;
        try
        {
            var dissection = CSharpCompilation.Create(
                "MeshWeaverEmitCanaryDissection",
                syntaxTrees: [CSharpSyntaxTree.ParseText(EmitCanarySource)],
                references: references(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            leaf = dissection.GetTypeByMetadataName("MwEmitCanary`1+Inner`1+Leaf`1");
            if (leaf is null)
                return "dissect=UNAVAILABLE(the canary source did not bind "
                    + "MwEmitCanary<T>.Inner<U>.Leaf<V>, so no read was attempted)";

            // The ordinary, public route to the very property the NRE is thrown from.
            var inner = leaf.ContainingType;
            var outer = inner?.ContainingType;

            // 🚨 THE THIRD READ, AND IT IS THE OPPOSITE POLARITY. The two reads above assert that a
            // non-null containing type reads non-null. The read `MetadataWriter` actually dies
            // behind is the other one: `AsNestedTypeDefinitionImpl`'s guard asks
            // `(object)ContainingType != null` on a type whose ContainingType is legitimately NULL
            // — the TOP-LEVEL one — and only calls `get_ContainingTypeDefinition` when that answers
            // TRUE. A guard that wrongly answers TRUE for a top-level type sends the writer
            // straight into `return this.ContainingType.GetCciAdapter()` on a genuine null, which
            // IS the #890 NRE, at the #890 frame, with no "two reads disagree" needed at all.
            // Omitting this read made `READS-HEALTHY` unearned: a process whose null-reads-non-null
            // is broken would still answer `symbol:OK` on the two reads above and be reported
            // healthy. Same defect as a control that only ever exercises the populated case.
            var topLevel = outer?.ContainingType;

            symbolLeg = ClassifySymbolReads(
                leafContainer: inner is not null,
                innerContainer: outer is not null,
                topLevelContainer: topLevel is not null);
        }
        catch (Exception bindError)
        {
            return $"dissect=UNAVAILABLE({bindError.GetType().Name} at {ThrowSite(bindError)} while "
                + "binding the canary source, so no read was attempted)";
        }

        // 🚨 The Cci leg is probed on LEAF — a genuinely NESTED type — and never on the top-level
        // one. On a HEALTHY process `get_ContainingTypeDefinition` throws exactly the #890
        // NullReferenceException at exactly the #890 frame when called on a top-level type, because
        // its ContainingType is correctly null. The frame is therefore NOT diagnostic on its own;
        // what is diagnostic is that the METADATA WRITER reached it. Probing the top-level type
        // here would manufacture that stack on every healthy process.
        var cciLeg = ProbeCciContainingTypeDefinition(leaf);

        if (symbolLeg.StartsWith("symbol:NON-NULL", StringComparison.Ordinal))
            return $"dissect=GUARD-READ-BROKEN {symbolLeg} {cciLeg} — the TOP-LEVEL source type's "
                + "ContainingType reads NON-NULL when it is null by construction, on a compilation "
                + "created AFTER the fault, with no emit involved. That is precisely the read "
                + "AsNestedTypeDefinitionImpl's guard makes, and a wrong TRUE there hands "
                + "MetadataWriter a top-level type to call ITypeDefinitionMember."
                + "ContainingTypeDefinition on — whose NRE on a genuine null IS #890, needing no "
                + "corrupted symbol and no divergent pair of reads. #890 reproduces here in ONE "
                + "property read: take this to dotnet/runtime";

        if (symbolLeg != "symbol:OK")
            return $"dissect=SYMBOL-GRAPH-BROKEN {symbolLeg} {cciLeg} — ContainingType reads NULL "
                + "for a nested SOURCE type on a compilation created AFTER the fault, through the "
                + "ordinary property and with no emit involved. The broken read is the property, "
                + "not the metadata writer; every consumer of a nested symbol in this process is "
                + "affected";

        if (cciLeg.StartsWith("cci:UNAVAILABLE", StringComparison.Ordinal))
            return $"dissect=UNAVAILABLE {symbolLeg} {cciLeg} — the public read succeeded but the "
                + "Cci leg could not be reached, so the discriminator did not run";

        if (cciLeg.StartsWith("cci:OK", StringComparison.Ordinal))
            return $"dissect=READS-HEALTHY {symbolLeg} {cciLeg} — ALL of it reads correctly on "
                + "symbols bound after the fault: nested ContainingType reads non-null, TOP-LEVEL "
                + "ContainingType reads NULL (the read AsNestedTypeDefinitionImpl's guard makes), "
                + "and the exact ITypeDefinitionMember.ContainingTypeDefinition frame every #890 "
                + "stack dies in answers correctly when called DIRECTLY. Neither a corrupted object "
                + "graph nor a wrong guard read predicts that; code that is wrong only when reached "
                + "from MetadataWriter's own call site — where both of those are INLINED into "
                + "getConsolidatedTypeParameters — does. That is what the split-arm "
                + "DOTNET_TieredPGO=0 re-run tests";

        return $"dissect=REPRODUCED-OUTSIDE-EMIT {symbolLeg} {cciLeg} — the public ContainingType "
            + "reads correctly while the Cci explicit interface implementation on the SAME symbol "
            + "does not, called directly. #890 reproduces here in ONE property call, with no emit, "
            + "no metadata writer and no PE stream: take these two readings to dotnet/runtime";
    }

    /// <summary>
    /// Reduces leg 3's three <c>ContainingType</c> reads on <c>MwEmitCanary&lt;T&gt;.Inner&lt;U&gt;.Leaf&lt;V&gt;</c>
    /// to one token. Pure, so every branch — including the two polarities — is unit-testable
    /// without a poisoned process, which is the only way the <c>NON-NULL</c> branch can be covered
    /// at all: a healthy Roslyn cannot be made to produce it.
    ///
    /// <para>🚨 <b>The third parameter is the whole point.</b> Two of the reads must come back
    /// PRESENT and the third must come back ABSENT, because a top-level type has no containing
    /// type — and that absent read is the one <c>AsNestedTypeDefinitionImpl</c>'s guard makes
    /// before the metadata writer calls the property that #890 dies in. A classification that took
    /// only the first two would answer <c>symbol:OK</c> for a process broken in exactly the
    /// direction that produces this defect.</para>
    /// </summary>
    /// <param name="leafContainer"><c>Leaf&lt;V&gt;.ContainingType</c> resolved — expected TRUE.</param>
    /// <param name="innerContainer"><c>Inner&lt;U&gt;.ContainingType</c> resolved — expected TRUE.</param>
    /// <param name="topLevelContainer"><c>MwEmitCanary&lt;T&gt;.ContainingType</c> resolved —
    /// expected FALSE: its container is the global namespace, which is not a type.</param>
    internal static string ClassifySymbolReads(
        bool leafContainer, bool innerContainer, bool topLevelContainer)
        => !leafContainer ? "symbol:NULL@Leaf.ContainingType"
            : !innerContainer ? "symbol:NULL@Inner.ContainingType"
            : topLevelContainer ? "symbol:NON-NULL@MwEmitCanary.ContainingType"
            : "symbol:OK";

    /// <summary>
    /// Calls <c>NamedTypeSymbol.Microsoft.Cci.ITypeDefinitionMember.get_ContainingTypeDefinition()</c>
    /// on the internal symbol behind <paramref name="publicSymbol"/> — the exact method on every
    /// #890 stack — and reports what it answered.
    ///
    /// <para>Reflection is unavoidable: <c>Microsoft.Cci</c> and the internal symbol model are both
    /// internal to Roslyn, and the public <c>ISymbol</c> wrapper does not implement the interface.
    /// Every step that can fail to RESOLVE reports <c>cci:UNAVAILABLE(why)</c> — distinct from
    /// <c>cci:NULL</c> (it resolved and answered null) and <c>cci:THREW</c> (it resolved and threw),
    /// because "I could not look" and "I looked and it was broken" must never share a token.</para>
    ///
    /// <para>The base-type walk matters: <c>UnderlyingSymbol</c> is declared on a base of the
    /// public wrapper, and <c>Type.GetProperty</c> does not return non-public members of base
    /// classes.</para>
    /// </summary>
    private static string ProbeCciContainingTypeDefinition(INamedTypeSymbol publicSymbol)
    {
        object? underlying = null;
        try
        {
            for (var declaring = publicSymbol.GetType(); declaring is not null && underlying is null;
                 declaring = declaring.BaseType)
            {
                underlying = declaring
                    .GetProperty(UnderlyingSymbolProperty,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?.GetValue(publicSymbol);
            }
        }
        catch (Exception reflectionError)
        {
            return $"cci:UNAVAILABLE({reflectionError.GetType().Name} reaching {UnderlyingSymbolProperty})";
        }

        if (underlying is null)
            return $"cci:UNAVAILABLE(no {UnderlyingSymbolProperty} on {publicSymbol.GetType().Name})";

        MethodInfo target;
        try
        {
            var implementation = underlying.GetType();
            var cci = implementation.GetInterfaces()
                .FirstOrDefault(i => i.FullName == CciTypeDefinitionMember);
            if (cci is null)
                return $"cci:UNAVAILABLE({implementation.Name} does not implement {CciTypeDefinitionMember})";

            var map = implementation.GetInterfaceMap(cci);
            var index = Array.FindIndex(map.InterfaceMethods,
                m => m.Name == ContainingTypeDefinitionGetter);
            if (index < 0)
                return $"cci:UNAVAILABLE({CciTypeDefinitionMember} has no {ContainingTypeDefinitionGetter})";
            target = map.TargetMethods[index];
        }
        catch (Exception mapError)
        {
            return $"cci:UNAVAILABLE({mapError.GetType().Name} mapping {CciTypeDefinitionMember})";
        }

        try
        {
            return target.Invoke(underlying, null) is null ? "cci:NULL" : "cci:OK";
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            var actual = wrapped.InnerException;
            return $"cci:THREW {actual.GetType().Name} at {ThrowSite(actual)}";
        }
        catch (Exception probeError)
        {
            return $"cci:THREW {probeError.GetType().Name} at {ThrowSite(probeError)}";
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
    ///
    /// <para>🚨 <b>A remedy nobody can execute is not a remedy.</b> The <c>BELOW-ROSLYN</c> branch
    /// told triage to *"capture a core dump and re-run with tiering disabled"* from 2026-08-28, and
    /// the dump half is <b>unfollowable by construction</b>: <c>DOTNET_DbgEnableMiniDump</c> fires
    /// on a SIGNAL, and this process never signals — it throws, logs, keeps running, and is killed
    /// by the harness wall-clock cap as <c>exit=124</c> (SIGTERM), which writes no dump. Nine
    /// occurrences carried that advice and produced exactly zero dumps. The branch now names the
    /// half that IS followable (a split-arm tiering re-run) and hands over leg 3's
    /// <c>dissect=</c> reading, which takes in-process the measurement the dump was being asked
    /// for. This is the same defect as a gate that cannot fail, worn as prose.</para>
    /// </summary>
    /// <param name="shared">Leg 1's outcome token — the same reference set as the failed compile.</param>
    /// <param name="pristine">Leg 2's outcome token — brand-new references.</param>
    /// <param name="dissect">
    /// Leg 3 (<see cref="DissectTheNull"/>), as a FACTORY so this method stays pure and every
    /// branch stays unit-testable. Invoked ONLY on the two verdicts that mean "the control could
    /// not emit either" — <c>BELOW-ROSLYN</c> and <c>DIVERGENT</c> — never on <c>REFERENCES</c>,
    /// where the pristine leg emitted and the symbol graph is demonstrably intact. <c>null</c>
    /// (the unit-test shape) reports <c>dissect=NOT-RUN</c> rather than silently omitting it: an
    /// absent reading must be visible as absent.
    /// </param>
    /// <param name="flat">
    /// The <c>flat=</c> leg as a FACTORY returning leg 1's token shape for
    /// <see cref="FlatCanarySource"/> — a single top-level, non-generic, member-less class emitted
    /// against the SAME references as <paramref name="shared"/>, so NESTING is the only variable
    /// between them. Gated exactly like <paramref name="dissect"/>. <c>null</c> reports
    /// <c>flat=NOT-RUN</c>: an absent reading must be visible as absent.
    /// </param>
    internal static string Verdict(
        string shared, string pristine, Func<string>? dissect = null, Func<string>? flat = null)
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
                + "whichever site is not the emit itself. " + Dissection(dissect)
                + " " + Flatness(flat, sharedSite);

        return $"canary=BELOW-ROSLYN shared:{shared} pristine:{pristine} — a trivial compilation "
            + "with freshly parsed source and an IMAGE-BACKED CoreLib (fresh managed bytes, "
            + "sharing neither the MetadataReference instances nor the mmap'd images of the "
            + $"shared set) cannot emit either, and BOTH legs died in the same frame ({sharedSite}) "
            + "⇒ nothing about the reference set OR its file mappings explains this; the "
            + "broken state is below Roslyn (CLR heap / JIT / GC), so no reference-set change can "
            + "fix it. 🚨 DO NOT go looking for a core dump: DOTNET_DbgEnableMiniDump fires on a "
            + "SIGNAL and this process never signals — it throws, logs, keeps running, and is "
            + "killed by the harness wall-clock cap (exit=124 = SIGTERM), which writes none. The "
            + "followable measurement is a SPLIT-ARM re-run with DOTNET_TieredPGO=0 (sharper) or "
            + "DOTNET_TieredCompilation=0 — at ~1% per run one clean arm proves nothing — read "
            + "together with the flat= and dissect= readings below. 🚨 READ flat= BEFORE acting on "
            + "this line: flat=EMITS means a top-level, non-generic, member-less class STILL emits "
            + "in this process, so 'cannot emit' is true of the workload (nested-generic "
            + "throughout) and not of emit as such, and the mechanism is confined to "
            + "GetConsolidatedTypeParameters' walk; flat=SAME-FRAME is the opposite and is the "
            + "one-method reproduction. RESIDUAL: both legs still run on the one "
            + "CLR, so this does not separate a corrupted heap from a miscompiled Roslyn method; "
            + "#613 is the SIGNALLING twin and is where a faulting address actually comes from. "
            + Dissection(dissect)
            + " " + Flatness(flat, sharedSite);
    }

    /// <summary>
    /// Runs leg 3 and reduces it to the token appended to the verdict, or says it did not run.
    /// Never throws: <paramref name="dissect"/> is already total, and a diagnostic that can fault
    /// while reporting a fault destroys the evidence it exists to preserve.
    /// </summary>
    private static string Dissection(Func<string>? dissect)
    {
        if (dissect is null)
            return "dissect=NOT-RUN (no probe supplied)";
        try
        {
            return dissect();
        }
        catch (Exception probeError)
        {
            return $"dissect=UNAVAILABLE({probeError.GetType().Name} — the probe itself faulted, "
                + "so it says nothing either way)";
        }
    }

    /// <summary>
    /// Runs the <c>flat=</c> leg and reduces it to the token appended to the verdict, or says it
    /// did not run. Never throws, for the same reason <see cref="Dissection"/> does not.
    /// </summary>
    private static string Flatness(Func<string>? flat, string? nestedSite)
    {
        if (flat is null)
            return "flat=NOT-RUN (no probe supplied)";
        try
        {
            return ClassifyFlatLeg(flat(), nestedSite);
        }
        catch (Exception probeError)
        {
            return $"flat=UNAVAILABLE({probeError.GetType().Name} — the probe itself faulted, "
                + "so it says nothing either way)";
        }
    }

    /// <summary>
    /// Reduces the <c>flat=</c> leg to one token. Pure — no Roslyn, no process state — so every
    /// branch is unit-testable without a poisoned process, which is the only way the branches that
    /// matter can be covered at all.
    ///
    /// <para>🚨 <b>What this leg varies, and why it is narrower than <c>dissect=</c>.</b> Leg 3
    /// probes symbol READS from a caller that is not the metadata writer, so a fault that is wrong
    /// only at the writer's own call site reads healthy there by construction — which is exactly
    /// what three unanimous <c>READS-HEALTHY</c> occurrences (2026-09-05, 09-06, 09-07) say. This
    /// leg stays inside a real <c>Emit</c> and varies ONE thing instead: whether the compilation
    /// contains a nested type at all. Same references as leg 1, same process, same moment.</para>
    /// </summary>
    /// <param name="flat">Leg 1's token shape, for <see cref="FlatCanarySource"/>.</param>
    /// <param name="nestedSite">
    /// The frame the NESTED leg died in, or <c>null</c> when it recorded none. The comparison is
    /// the whole discriminator: a flat emit dying somewhere ELSE is a second fault, not this one.
    /// </param>
    internal static string ClassifyFlatLeg(string flat, string? nestedSite)
    {
        if (flat.StartsWith("OK", StringComparison.Ordinal))
            return "flat=EMITS — a single TOP-LEVEL, non-generic, member-less class emits against "
                + "the SAME reference set, in the same process, microseconds after the nested "
                + "source could not. So this process is NOT emit-dead: the fault needs the "
                + "nested/generic walk (GetConsolidatedTypeParameters' recursion through "
                + "ContainingTypeDefinition), and AsNestedTypeDefinitionImpl's guard is answering "
                + "correctly for a top-level type. Read every 'PROCESS CANNOT EMIT' line as "
                + "'cannot emit THIS SHAPE' — the workload is nested-generic throughout, so the "
                + "blast radius is unchanged, but the mechanism is confined to the recursion";

        var site = SiteOf(flat);
        if (site is null)
            return $"flat=INCONCLUSIVE({flat}) — the flat leg neither emitted nor recorded a "
                + "throwing frame, so it cannot be compared with the nested leg and says nothing "
                + "either way";

        if (nestedSite is not null && string.Equals(site, nestedSite, StringComparison.Ordinal))
            return $"flat=SAME-FRAME@{site} — 🚨 the writer reached that frame with NO nested type "
                + "anywhere in the compilation. GetConsolidatedTypeParameters returns immediately "
                + "when AsNestedTypeDefinition answers null, and a member-less top-level class "
                + "leaves that guard as the only caller of the NamedTypeSymbol overload — so the "
                + "guard read TRUE where it must read FALSE. #890 reproduces here in ONE method, "
                + "with no recursion, no generics and no nesting: that is the report dotnet/runtime "
                + "needs, and it is what dissect=GUARD-READ-BROKEN could not see from outside emit";

        return $"flat=OTHER-FRAME@{site} — the flat leg failed too, but at a DIFFERENT frame than "
            + $"the nested leg ('{nestedSite ?? "an unrecorded site"}'). Two frames are two faults "
            + "until shown otherwise, so nothing is concluded about the guard; compare the sites";
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
    /// One canary leg: emit <paramref name="source"/> (<see cref="EmitCanarySource"/> unless the
    /// <c>flat=</c> leg overrides it) against the references
    /// <paramref name="references"/> produces, into memory, and reduce the outcome to a short
    /// token. Never throws — see the "cannot fail into the fault path it is diagnosing" note on
    /// <see cref="ProbeSharedEmitState"/>. The references arrive as a FACTORY so that building
    /// them is inside this method's try as well: on a poisoned process even
    /// <c>MetadataReference.CreateFromFile</c> is a candidate to fail.
    /// </summary>
    private static string EmitCanary(
        Func<IEnumerable<MetadataReference>> references, string source = EmitCanarySource)
    {
        try
        {
            var canary = CSharpCompilation.Create(
                "MeshWeaverEmitCanary",
                syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
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
    /// the caller to load. Like <see cref="EmitCompilationToDirectory(CSharpCompilation, string, string, string, CancellationToken)"/>, a failed emit throws
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
