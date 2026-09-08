using MeshWeaver.Data;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Roslyn produced the assembly, and the assembly store did not keep it — the compile's TERMINAL
/// failure when the bytes the record would name are not on the volume (a full share:
/// <see cref="Mesh.Persistence.ShortWriteException"/>) or the store faulted on the write.
///
/// <para>Why a compile that emitted fine ends in an exception rather than a warning: every
/// activation resolves the type's bytes THROUGH the store (<c>TryGetAssemblyPath</c> on the
/// record's <c>LatestAssemblyPath</c>), the local emit directory is not shared between replicas,
/// and on 2026-09-08 the old "settles Ok with a warning" contract minted three Release nodes for
/// three DLLs the same pod could not load. So the compile FAILS, <c>ApplyCompileFailure</c> writes
/// <c>CompilationStatus.Error</c> with the store's reason (the disk numbers ride in the inner
/// exception's message), <c>TryCreateReleaseNode</c> is skipped, and the previous build's
/// coordinates stay in place. It is an <see cref="IOException"/> so the park registry classifies
/// it as a NON-deterministic infra fault — retried within its bound, then parked with the reason.</para>
/// </summary>
public sealed class AssemblyPublicationException : IOException
{
    /// <summary>Creates the terminal failure for <paramref name="nodeTypePath"/>.</summary>
    /// <param name="nodeTypePath">The NodeType whose bytes did not land.</param>
    /// <param name="version">The store version the publication was keyed on.</param>
    /// <param name="storeFault">What the store reported.</param>
    /// <param name="log">The compile's own activity log up to the publication — Roslyn's transcript
    /// and warnings — so the activity node still shows what DID happen before the store refused.</param>
    public AssemblyPublicationException(string nodeTypePath, long version, IOException storeFault, ActivityLog? log)
        : base(
            $"The assembly store did not keep the compiled bytes for '{nodeTypePath}'@v{version}: "
            + storeFault.Message,
            storeFault)
    {
        NodeTypePath = nodeTypePath;
        Version = version;
        Log = log;
    }

    /// <summary>The NodeType whose bytes did not land.</summary>
    public string NodeTypePath { get; }

    /// <summary>The store version the publication was keyed on.</summary>
    public long Version { get; }

    /// <summary>The compile's activity log up to the refused publication, when the pipeline had one.</summary>
    public ActivityLog? Log { get; }
}
