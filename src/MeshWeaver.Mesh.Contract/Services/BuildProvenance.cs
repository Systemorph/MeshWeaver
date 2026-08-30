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
    /// An adoption was REFUSED because the bundle's recorded source fingerprint DISAGREED with the
    /// live source set. The bytes were not accepted; a local compile of the live source was
    /// driven instead. This is the data-loss case, caught.
    /// </summary>
    AdoptionRefused
}
