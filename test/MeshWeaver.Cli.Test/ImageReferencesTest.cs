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

        // The portal's `latest` moves on the ARMED set; the tester tag moved on that same set is
        // `<major>-latest` — never the tester's own `latest`, which moves at promotion.
        Assert.Equal("cr.meshweaver.cloud/mw-plugin-test:3-latest", images.TesterImage);
        Assert.Equal("cr.meshweaver.cloud/memex-portal-ai:latest", images.PlatformImage);
    }

    [Fact]
    public void ByDefault_TheTesterAndThePortalComeFromOneSet_NeverLatestPlusLatest()
    {
        var images = ImageReferences.Resolve(Env(), file: null);

        // Negative control: the mismatched pair (both on `latest`) — tester at PROMOTION, portal at
        // ARMING — is exactly what the default must not produce.
        Assert.False(images.TesterTag == "latest" && images.Tag == "latest",
            "the default paired mw-plugin-test:latest with memex-portal-ai:latest, which CD moves at different stages");
        Assert.Equal("3-latest", ImageReferences.TesterTagFor("latest", "3"));
        Assert.NotEqual("latest", ImageReferences.TesterTagFor("latest", "3"));
    }

    [Fact]
    public void ASetVersionOrLinePointer_NamesBothImagesAsWritten()
    {
        Assert.Equal("3.1.11786", ImageReferences.TesterTagFor("3.1.11786", "3"));
        Assert.Equal("3-latest", ImageReferences.TesterTagFor("3-latest", "3"));

        var images = ImageReferences.Resolve(Env((ImageReferences.EnvTag, "3.1.11786")), file: null);
        Assert.Equal("cr.meshweaver.cloud/mw-plugin-test:3.1.11786", images.TesterImage);
        Assert.Equal("cr.meshweaver.cloud/memex-portal-ai:3.1.11786", images.PlatformImage);
    }

    [Fact]
    public void ThePlatformMajorIsConfigurable()
    {
        var fromFile = ImageReferences.Resolve(Env(), new MemexConfig.ConfigFile { ImageMajor = "4" });
        Assert.Equal("cr.meshweaver.cloud/mw-plugin-test:4-latest", fromFile.TesterImage);

        var fromEnv = ImageReferences.Resolve(Env((ImageReferences.EnvMajor, "5")), new MemexConfig.ConfigFile { ImageMajor = "4" });
        Assert.Equal("5-latest", fromEnv.TesterTag);
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
