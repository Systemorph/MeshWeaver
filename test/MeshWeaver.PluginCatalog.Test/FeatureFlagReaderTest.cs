#pragma warning disable CS1591

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The FLAG SURFACE itself, pinned without a mesh: how the two authored shapes read, what an absent
/// or malformed <c>Enabled</c> means, and — the part that cannot be asserted by reading a property
/// back — that a configuration RELOAD pushes a new value to an existing subscriber.
///
/// <para>🚨 The reload assertion is the whole reason <see cref="IFeatureFlags"/> is reactive rather
/// than a <c>bool IsEnabled(string)</c>. A synchronous reader is indistinguishable from a correct
/// one at the moment it is first called, and wrong forever after the first reload; only a test that
/// subscribes BEFORE the change and asserts the SECOND emission can tell them apart.</para>
/// </summary>
public class FeatureFlagReaderTest
{
    private static IConfigurationRoot Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ImmutableSortedDictionary<string, FeatureFlag> Read(IConfiguration config)
    {
        using var flags = new ConfigurationFeatureFlags(config);
        return flags.All.FirstAsync().Wait();
    }

    [Fact]
    public void NoFlagsDeclared_IsEmpty_NotAnError()
    {
        // The platform default: an environment that says nothing declares nothing — and a mesh built
        // with no configuration at all must still resolve the surface rather than throwing.
        Read(Config()).Should().BeEmpty();
        using var none = new ConfigurationFeatureFlags(configuration: null);
        none.All.FirstAsync().Wait().Should().BeEmpty();
    }

    [Fact]
    public void DeclaringAFlagIsTheOptIn_AbsentEnabledMeansOn()
    {
        // Declaring the flag in an environment's values file IS the opt-in — the separate Enabled key
        // exists so a SHARED declaration can be switched off by one environment, not so every
        // environment has to repeat "yes I mean it".
        var flags = Read(Config(("Features:Flags:store:Packages:0", "Plugins/Store")));

        flags.Should().ContainKey("store");
        flags["store"].Enabled.Should().BeTrue();
        flags["store"].Packages.Should().Equal("Plugins/Store");
    }

    [Fact]
    public void UndeclaredFlagIsOff()
    {
        using var flags = new ConfigurationFeatureFlags(Config(("Features:Flags:store", "true")));
        flags.IsEnabled("never-declared").FirstAsync().Wait().Should().BeFalse();
        flags.Get("never-declared").FirstAsync().Wait().Should().BeNull();
    }

    [Fact]
    public void TheLeafShape_IsAPlainNamedBoolean()
    {
        // Features__Flags__betaChat=true — the cheapest thing to write in a values file, and the
        // whole point of a flag the platform's C# does not have to know about in advance.
        var flags = Read(Config(("Features:Flags:betaChat", "true"), ("Features:Flags:legacy", "false")));

        flags["betaChat"].Enabled.Should().BeTrue();
        flags["betaChat"].Packages.Should().BeEmpty();
        flags["legacy"].Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    public void ANonBooleanEnabledIsNotConsent(string value)
    {
        // Same rule PackageSources.Flag applies to the auto-sync switches: a value that is not a
        // boolean must never be read as "yes". These flags install content.
        Read(Config(("Features:Flags:store:Enabled", value))).GetValueOrDefault("store")!
            .Enabled.Should().BeFalse();
    }

    [Fact]
    public void FlagNamesAreCaseInsensitive_BecauseConfigurationKeysAre()
    {
        // Env-var delivery upper-cases and mangles casing on several platforms; the flag a values
        // file declares must be the flag the code asks about.
        using var flags = new ConfigurationFeatureFlags(Config(("Features:Flags:Store", "true")));
        flags.IsEnabled("store").FirstAsync().Wait().Should().BeTrue();
    }

    [Fact]
    public void TheMaintainersCase_AllOfPluginsWithoutTheGames()
    {
        // The acceptance criterion, expressed exactly as an operator would write it: ONE shared
        // declaration, and the environment that does not want the games flips a single key.
        var shared = new (string, string?)[]
        {
            ("Features:Flags:plugins:Packages:0", "Plugins/*"),
            ("Features:Flags:games:Packages:0", "Plugins/Chess"),
            ("Features:Flags:games:Packages:1", "Plugins/DoublePendulum"),
            ("Features:Flags:games:Packages:2", "Plugins/FractalStars"),
            ("Features:Flags:games:Packages:3", "Plugins/ThreeBody"),
        };

        // memex.meshweaver.cloud — everything.
        var everything = ConfigurationFeatureFlags.Compose(Read(Config(shared)));
        everything.Included.Select(p => p.Package).Should().Contain("Plugins/*");
        everything.Included.Should().HaveCount(5, "both flags are declared, so both are on");
        everything.Excluded.Should().BeEmpty();

        // memex.systemorph.com — the same, minus the games. ONE extra line.
        var withoutGames = ConfigurationFeatureFlags.Compose(Read(Config(
            [.. shared, ("Features:Flags:games:Enabled", "false")])));
        withoutGames.Included.Select(p => p.Package).Should().Equal("Plugins/*");
        withoutGames.Excluded.Select(p => p.Package).Should().Equal(
            "Plugins/Chess", "Plugins/DoublePendulum", "Plugins/FractalStars", "Plugins/ThreeBody");
        withoutGames.Excluded.Select(p => p.Flag).Should().AllBe("games",
            "an exclusion must name the flag an operator has to flip to undo it");
    }

    [Fact]
    public void AReloadPushesANewValueToAnEXISTINGSubscriber()
    {
        // 🚨 The assertion a synchronous `bool IsEnabled` cannot pass. Subscribe FIRST, change the
        // configuration, and require the SECOND emission — sampling after the change would pass
        // against a startup-snapshot reader too and prove nothing.
        var config = Config(("Features:Flags:store:Enabled", "false"));
        using var flags = new ConfigurationFeatureFlags(config);

        var seen = new List<bool>();
        using var subscription = flags.IsEnabled("store").Subscribe(seen.Add);
        seen.Should().Equal([false]);

        // Change the value in the provider, then reload — the same push a JSON provider makes when
        // its file changes (every appsettings source here is opened with reloadOnChange: true).
        config["Features:Flags:store:Enabled"] = "true";
        config.Reload();

        seen.Should().Equal([false, true],
            "a flag surface that does not re-emit is stale the moment configuration reloads");
    }

    [Fact]
    public void ComposeIsStableAndDeduplicated()
    {
        // The installer consumes this list; a shuffling or duplicated composition would make its
        // log line — and its "matched nothing" diagnosis — unreadable.
        var composition = ConfigurationFeatureFlags.Compose(Read(Config(
            ("Features:Flags:b:Packages:0", "Plugins/Store"),
            ("Features:Flags:a:Packages:0", "Plugins/Store"),
            ("Features:Flags:a:Packages:1", "Plugins/Store"))));

        composition.Included.Should().Equal(
            new FeaturePackage("a", "Plugins/Store"),
            new FeaturePackage("b", "Plugins/Store"));
    }
}
