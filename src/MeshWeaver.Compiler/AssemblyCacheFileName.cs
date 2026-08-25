using System.Globalization;

namespace MeshWeaver.Compiler;

/// <summary>
/// One cache file's identity, decoded from its name:
/// <c>v{version}-{frameworkTag}-{contentHash}.dll|.pdb</c>.
/// </summary>
/// <param name="Version">The <c>MeshNode.Version</c> the bytes were compiled for.</param>
/// <param name="Tag">The framework generation tag, lower-cased.</param>
/// <param name="Hash">The 12-char content hash.</param>
/// <param name="IsPdb">Whether this name is the debug symbols rather than the assembly.</param>
public readonly record struct AssemblyCacheFileIdentity(long Version, string Tag, string Hash, bool IsPdb);

/// <summary>
/// 🚨 <b>The ONE decoder of the assembly-cache file-name shape — and therefore the ONE deletion
/// boundary.</b>
///
/// <para><see cref="FileSystemAssemblyStore"/> writes
/// <c>{root}/{sanitized-nodeTypePath}/v{version}-{frameworkTag}-{contentHash}.dll</c> (+ <c>.pdb</c>).
/// Two independent collectors decide what they may remove from that tree — the per-type
/// eviction-at-write inside the store itself, and the per-generation sweep
/// (<c>AssemblyCacheGenerations</c> in MeshWeaver.Graph) — and BOTH must agree, exactly, on which
/// names this store wrote. A name that does not decode here is never attributed and therefore never
/// deleted by either: the bake-lease files, the generation claim files, the atomic-write
/// <c>.tmp-*</c> leftovers, pre-tag legacy DLLs, and anything a human dropped in the tree.</para>
///
/// <para><b>The widths are part of the shape, not decoration.</b> The store always emits an 8-char
/// tag and a 12-char hash, so accepting any hex length would let a foreign name like
/// <c>v1-ab-cd.dll</c> be attributed — and attribution is what makes a file deletable. Matching
/// exactly what the writer emits is what keeps "only files this store wrote are ever deleted"
/// literally true.</para>
///
/// <para>It lives beside the writer deliberately: the parser and the name-builder must change in the
/// same edit, and MeshWeaver.Graph already depends on MeshWeaver.Compiler, so the sweep can share
/// this while the store cannot have shared the sweep's.</para>
/// </summary>
public static class AssemblyCacheFileName
{
    /// <summary>
    /// Width of the framework tag the store writes — <c>FrameworkVersion[..8]</c>, see
    /// <see cref="FileSystemAssemblyStore.FrameworkTag"/>.
    /// </summary>
    public const int FrameworkTagLength = 8;

    /// <summary>
    /// Width of the content hash the store writes — 12 hex chars (<c>ToHexString</c> of the first
    /// 6 SHA-256 bytes), see <c>FileSystemAssemblyStore.ContentHash</c>.
    /// </summary>
    public const int ContentHashLength = 12;

    /// <summary>
    /// Decode a cache file name, or <c>null</c> when the name is not one this store wrote. Strict on
    /// purpose — everything this refuses is something no collector will ever delete.
    /// </summary>
    public static AssemblyCacheFileIdentity? Parse(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var isPdb = string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase);
        if (!isPdb && !string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
            return null;

        // v{version}-{frameworkTag}-{contentHash} — exactly three segments, no more, no less. A
        // pre-tag legacy name (v{version}-{hash}) has two and is therefore never attributed, so it
        // is never collected either.
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
        if (parts.Length != 3)
            return null;
        if (parts[0].Length < 2 || (parts[0][0] != 'v' && parts[0][0] != 'V'))
            return null;
        if (!long.TryParse(parts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return null;
        if (!IsGenerationTag(parts[1]))
            return null;
        if (parts[2].Length != ContentHashLength || !IsHex(parts[2]))
            return null;
        return new AssemblyCacheFileIdentity(
            version, parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant(), isPdb);
    }

    /// <summary>
    /// The framework generation a cache file name belongs to, or <c>null</c> when the name is not
    /// one this store wrote.
    /// </summary>
    public static string? TagOf(string fileName) => Parse(fileName)?.Tag;

    // The three tag shapes the store has ever written (FrameworkVersion[..8], see
    // FileSystemAssemblyStore.FrameworkTag): 8 hex chars for an MVID identity (local builds, and
    // every build before #1660 WS3), 'g' + 7 hex chars for a commit identity (manifest-less CI
    // processes since #1660 WS3), or 's' + 7 hex chars for the API-surface identity (hosts that
    // ship a surface manifest — the portals and the bake host). Anything else stays unattributed
    // and therefore undeletable.
    private static bool IsGenerationTag(string s) =>
        s.Length == FrameworkTagLength
        && (IsHex(s) || ((s[0] is 'g' or 'G' or 's' or 'S') && IsHex(s[1..])));

    private static bool IsHex(string s) =>
        s.Length > 0 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
