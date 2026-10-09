namespace MeshWeaver.Hosting;

/// <summary>
/// The file operations the published-bundle share walk performs, in ONE place, so the walk's IO is
/// a countable, injectable edge (<c>Doc/Architecture/CiContentBake</c> → "The published-root walk").
///
/// <para><b>Why it exists.</b> The published root lives on a shared network volume (on AKS the
/// Azure Files <c>/data</c> share), where every one of these calls is a round-trip. The adoption
/// pass in front of the bake sweep once spent minutes of every pod's boot in a walk whose COUNT of
/// round-trips grew with every identity the share held, and nothing in the log said so. Routing
/// the walk through this seam is what lets a test stand it on a SLOW share and assert how many
/// operations a no-op pass costs — the regression test the incident needed.</para>
///
/// <para>Production uses <see cref="Real"/>, which forwards to <see cref="File"/>/<see cref="Directory"/>
/// and holds no state. The walk still runs on the seeding pool's blocking leg
/// (<c>IoPoolRegistry.Get("prebuilt:files")</c>); this type changes WHAT is asked, not where.</para>
/// </summary>
internal class BundleShareIo
{
    /// <summary>The real file system. Stateless.</summary>
    public static readonly BundleShareIo Real = new();

    /// <summary><see cref="Directory.Exists(string)"/>.</summary>
    /// <param name="path">The directory path.</param>
    public virtual bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary><see cref="File.Exists(string)"/>.</summary>
    /// <param name="path">The file path.</param>
    public virtual bool FileExists(string path) => File.Exists(path);

    /// <summary>The subdirectories of <paramref name="path"/> — ONE listing, materialized.
    /// Throws <see cref="DirectoryNotFoundException"/> when the directory is absent.</summary>
    /// <param name="path">The directory to list.</param>
    public virtual IReadOnlyList<string> EnumerateDirectories(string path) =>
        [.. Directory.EnumerateDirectories(path)];

    /// <summary><see cref="File.ReadAllLines(string)"/>.</summary>
    /// <param name="path">The file path.</param>
    public virtual string[] ReadAllLines(string path) => File.ReadAllLines(path);

    /// <summary><see cref="File.ReadAllText(string)"/>.</summary>
    /// <param name="path">The file path.</param>
    public virtual string ReadAllText(string path) => File.ReadAllText(path);

    /// <summary>A bundle's manifest (<see cref="Plugin.Packaging.BundleReader.ReadManifest(string)"/>).</summary>
    /// <param name="bundlePath">The bundle archive.</param>
    public virtual Plugin.Packaging.BundleReader.Manifest? ReadManifest(string bundlePath) =>
        Plugin.Packaging.BundleReader.ReadManifest(bundlePath);
}
