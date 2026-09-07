namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The IDENTITY of a content degradation — an exception object attached to the diagnostic,
/// deliberately never thrown.</b>
///
/// <para><b>What it marks.</b> A <c>MeshNode</c>'s <c>Content</c> arrived as JSON, the
/// polymorphic reader could not resolve its <c>$type</c> against the reading hub's TypeRegistry,
/// and the mesh-wide <c>IMeshContentTypeRegistry</c> could not recover it either. The value crosses
/// the read boundary STILL untyped, so every downstream <c>Content is X</c> / <c>as X</c> silently
/// answers absent: a view renders empty, a reactive wait never completes. Nothing throws — a READ
/// must stay bad-data tolerant, and that is exactly why this class of defect is invisible.</para>
///
/// <para>🚨 <b>Why an exception for something that is not thrown.</b> Because carrying one is what
/// makes the diagnostic DURABLE, and durability here is a property of the CALL, not of the wording
/// or the level. A CI shard's authoritative sink — <c>_meshweaver-test-trace.log</c>, written by
/// <c>XUnitFileLogger.Log</c> → <c>TestTraceLog.AppendFault</c> — takes a record <b>if and only if</b>
/// <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c>. The degradation warnings passed no
/// exception, so they could reach that file by construction never; and the job log carries only the
/// output of tests that FAILED, while a degradation is overwhelmingly logged under a test that
/// PASSES. The consequence was measured in MeshWeaver#3625: the shard gate
/// <c>check-untyped-content.sh</c>, which greps that very directory, had <b>no way to fire</b> — a
/// permanently green check, indistinguishable from a passing one.</para>
///
/// <para>This is the third recorded instance of the same trap (#890's <c>PROCESS CANNOT EMIT</c>,
/// #3413's <c>never rendered</c>, and this one) — see
/// <c>Doc/Architecture/ReadingCiSignals</c> → "for a fault the trace log is the sink".</para>
///
/// <para>🚨 <b>The gate keys on this TYPE NAME, not on the message.</b> A prose phrase is a
/// description of the event; the type is the event's identity, bound by the compiler at every
/// construction site. That gives the coupling two independent bindings instead of one: renaming the
/// type is a compile-wide change, and <c>UntypedContentDegradationGate</c> pins the script's key to
/// <c>nameof(MeshNodeContentDegradedException)</c> so a rename that forgets the script reds.</para>
///
/// <para>Constructing an exception without throwing it costs one allocation and captures no stack,
/// so <see cref="System.Exception.ToString"/> yields exactly <c>type: message</c> — which is the
/// whole record a reader needs here, since the fault is in the DATA and not in a call path.</para>
/// </summary>
public sealed class MeshNodeContentDegradedException : System.Exception
{
    /// <summary>The read seam that observed the degradation (e.g. <c>MeshNodeStreamCache.GetStream</c>).</summary>
    public string Seam { get; }

    /// <summary>The node whose content stayed untyped.</summary>
    public string? NodePath { get; }

    /// <summary>The node's NodeType, which is the key the content-type registry recovery is attempted under.</summary>
    public string? NodeType { get; }

    /// <summary>Truncated raw JSON of the content that could not be materialised.</summary>
    public string? RawJson { get; }

    /// <summary>Creates the degradation marker.</summary>
    /// <param name="seam">The read seam that observed it.</param>
    /// <param name="nodePath">The node path.</param>
    /// <param name="nodeType">The node's NodeType.</param>
    /// <param name="rawJson">Truncated raw JSON of the unreadable content.</param>
    /// <param name="inner">The deserialization exception, when there was one.</param>
    public MeshNodeContentDegradedException(
        string seam, string? nodePath, string? nodeType, string? rawJson, System.Exception? inner = null)
        : base(
            $"{seam}: content for '{nodePath ?? "<unknown>"}' (nodeType '{nodeType ?? "<none>"}') "
            + "stayed an untyped JsonElement — the $type discriminator resolved to no registered "
            + "CLR type on the reading hub and the mesh-wide content-type registry did not recover "
            + "it. Downstream 'Content is X' / 'as X' consumers read it as ABSENT: views render "
            + $"empty and reactive waits never complete. Raw: {rawJson ?? "<none>"}",
            inner)
    {
        Seam = seam;
        NodePath = nodePath;
        NodeType = nodeType;
        RawJson = rawJson;
    }
}
