using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>The MVID of the bytes — so "compiled Ok" can be CHECKED instead of believed.</b>
///
/// <para>A portal can serve STALE compiled code while reporting <c>Ok</c>/compiled, and survive
/// every remedy the product offers: recycle the NodeType, recycle the instance, press Compile —
/// each reports success and changes nothing, and <c>get_diagnostics</c> keeps saying <c>Ok</c>
/// (Systemorph/MeshWeaver#2471; memex, 2026-08-26, over 30+ minutes and six recycles). A claim of
/// <c>Ok</c> that is not backed by the bytes actually being served is a <b>lie with a green
/// tick</b>, which is strictly worse than an error: a merged, published, correct fix becomes
/// indistinguishable from one that is live, and the only honest check left is rendering the screen
/// and counting what comes back — exactly what a compile status is supposed to save you from.</para>
///
/// <para><b>Why the existing check cannot see it.</b> The stale-build watcher compares
/// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> against the path an instance bound. A PATH is
/// not an identity: the store key is <c>(nodeTypePath, LastCompiledVersion)</c>, a recompile of an
/// already-<c>Ok</c> type does not rewrite its node, and a pod resolves those bytes through its own
/// local cache — so the path can match perfectly while the bytes behind it differ per replica. That
/// is why a recycle is inert: it re-binds the same path from the same local copy.</para>
///
/// <para><b>The MVID is the identity.</b> A module version id is minted per emitted assembly, so two
/// builds of the same sources have different MVIDs and the same bytes always have the same one.
/// Recording it beside the path turns "which build is this?" from a guess into a comparison —
/// the same move the platform already makes with <c>framework-mvid.txt</c> beside a bake and the
/// <c>_complete</c> sentinel beside a publication: <b>a postcondition is asserted, never hoped
/// for.</b></para>
///
/// <para>🚨 <b>Metadata only — nothing is loaded.</b> Reading through <see cref="PEReader"/> costs a
/// file open and a table lookup, takes no ALC, and cannot run the assembly's initializers. Loading
/// it to ask for <c>ManifestModule.ModuleVersionId</c> would pin bytes this process may be about to
/// replace, which is how an assembly-cache leak starts.</para>
///
/// <para>Every reader here DEGRADES TO NULL rather than throwing: an unreadable or absent file is
/// "I do not know", and <see cref="Mismatch"/> treats "I do not know" as no evidence — never as a
/// mismatch. A detector that faults an activation would be a worse defect than the one it detects.</para>
/// </summary>
public static class ServedBuildIdentity
{
    /// <summary>
    /// The MVID of the assembly at <paramref name="path"/>, lower-case hex without separators
    /// (the <c>"N"</c> format the framework identity already uses), or null when the file is
    /// absent, unreadable, or not a managed PE.
    /// </summary>
    public static string? OfFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The MVID of an in-memory assembly image — the shape a bundle adoption has in hand, where no
    /// file exists yet. Null when <paramref name="assembly"/> is empty or not a managed PE.
    /// </summary>
    public static string? OfBytes(byte[]? assembly)
    {
        if (assembly is null || assembly.Length == 0)
            return null;
        try
        {
            using var stream = new MemoryStream(assembly, writable: false);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one-line account of a served build that is NOT the published one, or null when there is
    /// no evidence of a mismatch.
    ///
    /// <para>🚨 <b>Null on ANY unknown, deliberately.</b> A node stamped before this field existed
    /// carries no published MVID, and a producer without a store carries no path to read a served
    /// one; treating either absence as a mismatch would fire the banner on every legacy node on the
    /// first boot after this ships — a detector that cries wolf is uninstalled within the day, and
    /// then the real signal is gone too. Absence of a "yes" is not a "no": it is silence, and
    /// silence is reported by <see cref="Unverifiable"/> instead.</para>
    /// </summary>
    /// <param name="publishedMvid">The MVID the NodeType records for the build it published.</param>
    /// <param name="servedMvid">The MVID of the bytes this instance actually bound.</param>
    /// <param name="nodeType">The NodeType path, named in the message.</param>
    public static string? Mismatch(string? publishedMvid, string? servedMvid, string nodeType)
        => string.IsNullOrEmpty(publishedMvid)
           || string.IsNullOrEmpty(servedMvid)
           || string.Equals(publishedMvid, servedMvid, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"'{nodeType}' reports a compiled build with MVID {publishedMvid}, but the bytes this "
              + $"instance bound are MVID {servedMvid}. The node's status is therefore NOT evidence "
              + "about what is being served: this hub is executing a DIFFERENT build from the one "
              + "the type published. A recycle re-binds the same local copy and will not change it "
              + "(Systemorph/MeshWeaver#2471).";

    /// <summary>
    /// Whether the comparison could not be taken at all — one side unknown. Distinct from "they
    /// match": a caller that reports coverage must be able to say how many types it could not
    /// check, or an all-green count over an all-unknown fleet reads as proof.
    /// </summary>
    public static bool Unverifiable(string? publishedMvid, string? servedMvid) =>
        string.IsNullOrEmpty(publishedMvid) || string.IsNullOrEmpty(servedMvid);

    private static string? Read(Stream stream)
    {
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return null;
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
    }
}
