using System.Reflection;
using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// The images <c>memex build plugin</c> runs by default come from CONFIGURATION — the environment,
/// then <c>~/.memex/config.json</c>, then the neutral <c>latest</c> label on the fleet registry —
/// and never from the CLI's own version. A flag always wins.
/// </summary>
public class ImageReferencesTest
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void NothingConfigured_UsesLatestOnTheFleetRegistry()
    {
        var images = ImageReferences.Resolve(Env(), file: null);

        Assert.Equal("cr.meshweaver.cloud/mw-plugin-test:latest", images.TesterImage);
        Assert.Equal("cr.meshweaver.cloud/memex-portal-ai:latest", images.PlatformImage);
    }

    [Fact]
    public void TheDefaultTagIsNotTheCliVersion()
    {
        var cli = typeof(ImageReferences).Assembly;
        var informational = cli.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var version = cli.GetName().Version!;

        var images = ImageReferences.Resolve(Env(), file: null);

        Assert.Equal("latest", images.Tag);
        Assert.DoesNotContain(informational.Split('+')[0], images.PlatformImage, StringComparison.Ordinal);
        Assert.DoesNotContain($"{version.Major}-latest", images.PlatformImage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigFileSetsRegistryAndTag()
    {
        var file = new MemexConfig.ConfigFile { ImageRegistry = "registry.example.test/", ImageTag = "3.1.11786" };

        var images = ImageReferences.Resolve(Env(), file);

        Assert.Equal("registry.example.test/mw-plugin-test:3.1.11786", images.TesterImage);
        Assert.Equal("registry.example.test/memex-portal-ai:3.1.11786", images.PlatformImage);
    }

    [Fact]
    public void TheEnvironmentOverridesTheConfigFile()
    {
        var file = new MemexConfig.ConfigFile { ImageRegistry = "file.example.test", ImageTag = "from-file" };

        var images = ImageReferences.Resolve(
            Env((ImageReferences.EnvRegistry, "env.example.test"), (ImageReferences.EnvTag, "from-env")), file);

        Assert.Equal("env.example.test/memex-portal-ai:from-env", images.PlatformImage);
    }

    [Fact]
    public void BlankValuesCountAsUnset()
    {
        var file = new MemexConfig.ConfigFile { ImageRegistry = " ", ImageTag = "" };

        var images = ImageReferences.Resolve(Env((ImageReferences.EnvTag, "  ")), file);

        Assert.Equal("cr.meshweaver.cloud/memex-portal-ai:latest", images.PlatformImage);
    }

    [Fact]
    public void AFlagWinsOverTheConfiguredDefault()
    {
        var images = ImageReferences.Resolve(Env((ImageReferences.EnvTag, "from-env")), file: null);

        Assert.Equal("cr.meshweaver.cloud/memex-portal-ai@sha256:abc",
            ImageReferences.Or("cr.meshweaver.cloud/memex-portal-ai@sha256:abc", images.PlatformImage));
        Assert.Equal(images.PlatformImage, ImageReferences.Or(null, images.PlatformImage));
        Assert.Equal(images.PlatformImage, ImageReferences.Or("", images.PlatformImage));
    }
}
