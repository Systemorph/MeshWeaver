using System;
using System.IO;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>The image states its own framework identity, and it is the identity the process runs</b>
/// (MeshWeaver#6052 ask 3).
///
/// <para>The chart's <c>bundle-fetch</c> init container pulls the sealed plugin publication tagged
/// with the framework identity. That identity came from <c>bundles.identity</c>, a value fixed per
/// helm release, so a roll that moved only the portal's image left the fetch naming the previous
/// image's identity. The build now writes <c>meshweaver-framework.identity</c> beside the binaries
/// (<c>MeshWeaverSurfaceManifest.targets</c>) and the chart reads it out of the running image.</para>
///
/// <para>That only helps if the file says what the process will compute — the compatibility key
/// <see cref="FrameworkBuildIdentity.FrameworkVersion"/>. The build derives it from MSBuild facts
/// (the referenced MeshWeaver.Compiler's version, the declared epoch); the runtime derives it from
/// the loaded assembly's metadata. This pins the two to one value.</para>
/// </summary>
public class FrameworkBuildIdentityFileTest
{
    [Fact]
    public void TheIdentityFileBesideTheBinaries_IsTheIdentityTheProcessRuns()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "meshweaver-framework.identity");

        Assert.True(File.Exists(file), $"the build wrote no {file}");
        var written = File.ReadAllText(file).Trim();
        Assert.True(PlatformCompatibility.IsKey(written), $"'{written}' is not a compatibility key");
        Assert.Equal(FrameworkBuildIdentity.FrameworkVersion, written);
    }
}
