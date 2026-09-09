namespace MeshWeaver.Mesh.Services;

/// <summary>
/// WHERE a NodeType's current build came from, and — for an adopted one — whether the bytes were
/// ever checked against the source the node is holding.
///
/// <para>🚨 <b>Why this has to be on the record and not in a log.</b> An adopted assembly was
/// indistinguishable from a compiled one by every signal an operator was taught to trust:
/// <see cref="CompilationStatus.Ok"/>, and <c>CompiledSources == CurrentSourceVersions</c> (i.e.
/// <c>IsDirty == false</c>). Both read clean — because the adoption itself WRITES the second one
/// (<c>NodeTypeDefinition.RequestedSourceStampAt</c> asks the owner to stamp
/// <c>CompiledSources</c> from its own live snapshot). So the staleness detector was not broken;
/// it was answering a question the adoption had already answered for it. On 2026-08-30 that cost a
/// client four documents' bodies, one of them unrecoverable, because a GitSync <c>update</c>
/// adopted a prebuilt built from older source than the commit it had just pulled, and every check
/// available said the fix was live (MeshWeaver#2813).</para>
///
/// <para>The verdict a control plane needs before it arms anything is therefore not "is this Ok"
/// but "was this build ever compared to the source" — which is exactly what this states.</para>
///
/// <para>🚨 <see cref="AdoptedUnverified"/> is NEVER silently equivalent to
/// <see cref="AdoptedVerified"/>. A bundle published before producers recorded a source
/// fingerprint carries none, so its provenance is <b>unknown</b>, not <b>proven stale</b> — those
/// deserve different answers, and refusing the unknown one would break every bundle published to
/// date and the node-repo CI gates that depend on prebuilt fetches. It is adopted and MARKED;
/// only a fingerprint that is present and DISAGREES is refused.</para>
///
/// <para>🚨 The one refusal is keyed on MODULE VERSION COMPATIBILITY, not on fingerprint
/// equality (MeshWeaver#3583): a fingerprint that differs says the source MOVED; whether the
/// build that is serving may keep serving is answered by <see cref="ModuleVersionCompatibility"/>
/// — same MAJOR (or unknown) keeps it, as <see cref="StaleAdopted"/>; a MAJOR bump refuses it, as
/// <see cref="AdoptionRefused"/>. Either way the type never ERRORS because of delivery.</para>
///
/// <para>Appended-only: the persisted ordinal of every existing member must stay unchanged, and
/// <see cref="Compiled"/> is deliberately the zero value so a record written before this field
/// existed reads as the honest default — nothing was adopted, so nothing is unverified.</para>
/// </summary>
public enum BuildProvenance
{
    /// <summary>
    /// Roslyn compiled it here, from the source this mesh holds. The default, and what every
    /// record written before this field existed reads as.
    /// </summary>
    Compiled,

    /// <summary>
    /// Adopted from a prebuilt bundle whose recorded source fingerprint MATCHED the live source
    /// set at the moment the owner stamped it. The bytes and the source have been compared and
    /// they agree.
    /// </summary>
    AdoptedVerified,

    /// <summary>
    /// Adopted from a prebuilt bundle that carries NO source fingerprint — a legacy bundle. The
    /// bytes may or may not correspond to the live source; nothing has compared them.
    ///
    /// <para>🚨 This is the state in which <c>CompiledSources == CurrentSourceVersions</c> is
    /// true without having been earned. Read it as "unknown provenance", never as a clean bill of
    /// health, and do not arm a control plane against it without a deliberate decision.</para>
    /// </summary>
    AdoptedUnverified,

    /// <summary>
    /// An adoption was REFUSED: the bundle's recorded source fingerprint DISAGREES with the live
    /// source set AND the two sides are INCOMPATIBLE by module version — the current source's
    /// MAJOR differs from the adopted build's (<see cref="ModuleVersionCompatibility"/>). The bytes
    /// are not run (the execute-time gate refuses them); on a mesh that compiles module content a
    /// local compile of the live source is driven, on one that does not the type reports
    /// "incompatible, awaiting bundle" until a bundle for this identity lands. This is the
    /// data-loss case, caught.
    ///
    /// <para>🚨 Since MeshWeaver#3583 a fingerprint that merely DIFFERS no longer lands here — it
    /// lands on <see cref="StaleAdopted"/>. On 2026-09-09 a one-line CSS change to a module's
    /// source, synced ahead of its bundle, refused the adopted build on every instance of
    /// <c>Essentials/Email</c> on a live portal and rendered every page of the type dead for the
    /// afternoon. The refusal was right about the bytes (they were older) and wrong about the
    /// consequence: a page that renders last week's styling is a page; a refusal overlay is not.
    /// The portal owner's rule: only a declared incompatibility — a MAJOR bump — may refuse the
    /// last build the mesh holds.</para>
    /// </summary>
    AdoptionRefused,

    /// <summary>
    /// The LAST build this mesh holds keeps serving while the current source has moved PAST it —
    /// the adopted (or locally compiled) bytes are behind the source by a compatible amount (same
    /// module MAJOR, or the versions are not known), and no bundle for the running framework
    /// identity has caught up yet (MeshWeaver#3583, measured 2026-09-09).
    ///
    /// <para>Honest by construction: <c>CompiledSources</c> is cleared so <c>IsDirty</c> reads
    /// true (the build IS behind the source), <c>CompilationStatus</c> stays <c>Ok</c> (there IS a
    /// usable build, and it is not a compile that failed), and the NodeType page names both sides
    /// — adopted module version and source fingerprint against current ones — with "waiting for a
    /// bundle for this identity". The fingerprint remains the signal that the source moved; it
    /// drives this status and the readiness notification, never a refusal.</para>
    ///
    /// <para>Permitted by <c>NodeTypeExecutionGate</c>: stale-but-compatible bytes over newer
    /// source is the accepted trade-off; a dead page is not. Lifted by the next adoption whose
    /// fingerprint matches (→ <see cref="AdoptedVerified"/>, announced) or by a local compile of
    /// the live source (→ <see cref="Compiled"/>).</para>
    /// </summary>
    StaleAdopted
}
