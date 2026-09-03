using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// 🚨 <b>There is NO fallback to the tester's <c>/app</c></b> (#3113). <c>memex build plugin</c>
/// refuses when no PORTAL image is supplied, exactly as <c>node-repo-gate.yml</c> does
/// (<i>"platform-image-digest is empty and allow-unpinned is not set … There is no fallback to the
/// tester's /app"</i>).
///
/// <para>Silently compiling against the tester's subset reference set is not backwards
/// compatibility, it IS the defect: it produces a green run for content that binds nothing
/// portal-only and a <c>CS0234</c> on content that does, attributed to the content. A refusal is
/// legible; a wrong reference set is not.</para>
///
/// <para>🚨 Each case asserts that <b>nothing was run</b> as well as the exit code. <see
/// cref="ImageRunner"/> echoes every process it starts to stdout (<c>$ docker pull …</c>), so an
/// empty stdout is the positive evidence that the refusal happened BEFORE the first pull — the
/// property that makes a misconfigured job fail in seconds naming its input rather than minutes
/// later inside a container. The verb's first version put a check after stage 1 and the "proof" of
/// its refusal path had actually died at the container entrypoint before ever reaching it.</para>
/// </summary>
public class PlatformImageIsRequiredTest : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"mw-cli-repo-{Guid.NewGuid():N}");
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public PlatformImageIsRequiredTest() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task<int> Run(string? platformImage) =>
        new BuildPluginCommand(_out, _err).RunAsync(
            new BuildPluginOptions(_repo, "meshweaver.azurecr.io/mw-plugin-test@sha256:deadbeef")
            { PlatformImage = platformImage },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task WithoutAPlatformImage_TheBuildIsRefusedBeforeAnythingIsPulled()
    {
        Assert.Equal(9, await Run(null));

        var message = _err.ToString();
        Assert.Contains("--platform-image is required", message);
        // The refusal must say WHY, or the next reader "fixes" it by removing the argument again.
        Assert.Contains("no fallback to the tester's", message);
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Fact]
    public async Task AnEmptyPlatformImage_IsTheSameAsNone()
    {
        Assert.Equal(9, await Run(""));
        Assert.Contains("--platform-image is required", _err.ToString());
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Fact]
    public async Task TheTesterImagePassedAsThePlatform_IsRefusedByName()
    {
        Assert.Equal(9, await Run("meshweaver.azurecr.io/mw-plugin-test@sha256:deadbeef"));

        var message = _err.ToString();
        Assert.Contains("names the TESTER image", message);
        Assert.Contains("memex-portal-ai", message);
        Assert.Equal(string.Empty, _out.ToString());
    }

    /// <summary>
    /// The refusals above must not shadow the checks that already ran first — a plugin path that
    /// does not exist is still the first thing reported, and it is reported as itself.
    /// </summary>
    [Fact]
    public async Task AMissingPluginPath_IsStillTheFirstThingReported()
    {
        var rc = await new BuildPluginCommand(_out, _err).RunAsync(
            new BuildPluginOptions(
                Path.Combine(_repo, "nope"), "meshweaver.azurecr.io/mw-plugin-test@sha256:deadbeef"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rc);
        Assert.Contains("does not exist", _err.ToString());
    }
}
