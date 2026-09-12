namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>A content degradation that NEVER RECOVERED — the verdict, as opposed to
/// <see cref="MeshNodeContentDegradedException"/>, which is the event.</b> Constructed once per
/// node type when a mesh ends with that type still unresolvable, and deliberately never thrown.
///
/// <para><b>Why two markers (#3645).</b> The degradation marker is emitted at the INSTANT of a
/// read, and at that instant nothing can know whether the type will register a moment later. That
/// is the normal state during a portal boot — a NodeType's runtime compile lands after the first
/// readers have already been served — and it is precisely the race #2952 fixed by re-typing every
/// live reader when the registration arrives. The sibling seam one layer down
/// (<c>MeshNodeTypeSource</c>) says so in its own message: <i>"…is not a registered type on the
/// owning hub — content stays an untyped JsonElement FOR NOW … (a) the NodeType's runtime compile
/// has not registered it YET, which is TRANSIENT … or (b) no declaration will ever claim this
/// discriminator"</i>.</para>
///
/// <para>So the two seams described one event and disagreed about whether it was a failure: the
/// shard gate <c>check-untyped-content.sh</c> reddened on any occurrence, which made it report a
/// normal transient as a defect. Measured on #3628: two of
/// <c>LateContentTypeRegistrationTest</c>'s three cases degrade and RECOVER inside a single test —
/// the recovery IS the assertion — and both produced a gate-reddening record. The gate was worth
/// having and its denominator was wrong: a hit meant <i>"content was unreadable at a read"</i>,
/// never <i>"content is unreadable"</i>.</para>
///
/// <para><b>This marker is the second one.</b> <c>ContentDegradationRegistry</c> keeps what a
/// replica could not type; at the end of the mesh's life it RE-ASKS the mesh-wide content-type
/// registry about each entry (<c>TryResolveByNodeType</c> / <c>TryResolveByDiscriminator</c> — pure
/// lookups, no content needed) and reports only what is still unresolvable. A boot that read before
/// its compile landed leaves nothing; a discriminator no declaration will ever claim leaves exactly
/// one record, naming the node type. That is the sentence the gate keys on.</para>
///
/// <para>🚨 <b>The exception argument is load-bearing</b>, for the same reason it is on the
/// degradation marker: a CI shard's authoritative sink takes a record <b>if and only if</b>
/// <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c>, so a report without one could
/// reach <c>collected-logs/</c> by construction never (#3625). And the gate keys on this TYPE NAME
/// rather than on prose: the type is bound by the compiler at its construction site, and
/// <c>UntypedContentDegradationGate</c> pins the script's key to
/// <c>nameof(MeshNodeContentUnresolvedException)</c> so a rename that forgets the script reds.</para>
/// </summary>
public sealed class MeshNodeContentUnresolvedException : System.Exception
{
    /// <summary>The NodeType whose content type never became resolvable on this replica.</summary>
    public string NodeType { get; }

    /// <summary>The stored <c>$type</c> discriminator, when the degraded content carried one.</summary>
    public string? Discriminator { get; }

    /// <summary>How many reads degraded on it.</summary>
    public int Count { get; }

    /// <summary>The most recent node path read under it.</summary>
    public string? LastPath { get; }

    /// <summary>The read seam that observed it first.</summary>
    public string Seam { get; }

    /// <summary>Creates the unresolved-at-end verdict marker.</summary>
    /// <param name="nodeType">The NodeType that never resolved.</param>
    /// <param name="discriminator">The stored <c>$type</c>, when there was one.</param>
    /// <param name="count">How many reads degraded.</param>
    /// <param name="lastPath">The most recent node path.</param>
    /// <param name="seam">The seam that observed it first.</param>
    public MeshNodeContentUnresolvedException(
        string nodeType, string? discriminator, int count, string? lastPath, string seam)
        // 🚨 The gate's phrase — "was NEVER resolvable on this replica" — is kept CONTIGUOUS in one
        // literal on purpose. UntypedContentDegradationGate greps the SOURCE line by line, so a
        // phrase the formatter splits across two literals is emitted correctly and yet reads as
        // absent to the guard: a rewording that "passes" while retiring the coupling.
        : base(
            $"content for nodeType '{nodeType}' (discriminator '{discriminator ?? "<none>"}') "
            + $"was NEVER resolvable on this replica: {count} read(s) degraded to an untyped "
            + $"JsonElement (first seen at {seam}, last {lastPath ?? "<unknown>"}) and the type had "
            + "STILL not registered when the mesh ended. This is NOT the transient boot race — the "
            + "registry was re-asked at the end and answered no. The module that declares the type "
            + "is not loaded here, or no declaration claims that discriminator at all, so every "
            + "'Content is X' / 'as X' consumer reads it as ABSENT: views render empty and reactive "
            + "waits never complete.")
    {
        NodeType = nodeType;
        Discriminator = discriminator;
        Count = count;
        LastPath = lastPath;
        Seam = seam;
    }
}
