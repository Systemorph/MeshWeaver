using System.IO;
using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins WHICH version a plugin package carries.
///
/// <para>🚨 <b>Two files declare a version and they legitimately disagree.</b>
/// <c>index.json</c>'s <c>content.version</c> is the AUTHORED <c>MAJOR.MINOR</c>;
/// <c>manifest.lock</c>'s <c>version</c> is the composed release number, whose <c>PATCH</c> is
/// DERIVED by <c>gen-manifests.py</c> from content changes and published as the git tag
/// <c>&lt;Module&gt;/vX.Y.Z</c>. ThreeBody reads <c>1.3</c> in one and <c>1.3.2</c> in the other,
/// and only the second is a release anyone can resolve.</para>
///
/// <para>Reading the authored field alone mints a version the repo never released — colliding with
/// a real earlier release and making every caret pin resolve to the wrong content, which is the
/// precise failure that versioning scheme exists to prevent. It is also invisible: the pack
/// succeeds and the number looks plausible.</para>
/// </summary>
public class PluginManifestVersionTest : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("mw-plugin-version").FullName;

    private string Plugin(string? authored, string? locked)
    {
        var dir = Path.Combine(root, "Widget");
        Directory.CreateDirectory(dir);
        var versionField = authored is null ? "" : ",\"version\":\"" + authored + "\"";
        File.WriteAllText(Path.Combine(dir, "index.json"),
            "{\"id\":\"Widget\",\"content\":{\"$type\":\"PluginContent\",\"description\":\"d\""
            + versionField + "}}");
        if (locked is not null)
            File.WriteAllText(Path.Combine(dir, "manifest.lock"),
                "{\"module\":\"Widget\",\"version\":\"" + locked + "\",\"moduleVersion\":\"abc123\"}");
        return dir;
    }

    [Fact]
    public void TheLockWinsOverTheAuthoredVersion()
        // The whole point: the derived PATCH must survive into the package.
        => Assert.Equal("1.3.2", PluginManifest.Read(Plugin(authored: "1.3", locked: "1.3.2"), "0.0.1").Version);

    [Fact]
    public void TheAuthoredVersionIsUsedWhenThereIsNoLock()
        // A plugin whose manifest has not been generated yet still packs, widened to three parts.
        => Assert.Equal("1.3.0", PluginManifest.Read(Plugin(authored: "1.3", locked: null), "0.0.1").Version);

    [Fact]
    public void TheFallbackIsUsedWhenNeitherDeclaresOne()
        => Assert.Equal("0.0.1", PluginManifest.Read(Plugin(authored: null, locked: null), "0.0.1").Version);

    [Fact]
    public void AMalformedLockFallsBackRatherThanFailingThePack()
    {
        var dir = Plugin(authored: "2.1", locked: null);
        File.WriteAllText(Path.Combine(dir, "manifest.lock"), "{ NOT JSON");

        // The lock is machine-maintained; a broken one is the generator's problem to report, and
        // failing the pack here would block a plugin for a defect in a different tool.
        Assert.Equal("2.1.0", PluginManifest.Read(dir, "0.0.1").Version);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
