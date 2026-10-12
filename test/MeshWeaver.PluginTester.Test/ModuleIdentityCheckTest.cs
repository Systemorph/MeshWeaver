using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// #3583 — the verb the publish-bake lane's <c>previous</c> leg runs before it seals a CARRIED-
/// FORWARD module set for the previous compatibility epoch: every bundle must state that it was
/// built for the declared <c>previousEpochCeiling</c>'s identity, by
/// <c>ModuleFrameworkIdentity</c>'s rule. A bundle this run built for the CURRENT epoch is the
/// one the gap in #3583 would have sealed under the old key, and it must be refused.
/// </summary>
public class ModuleIdentityCheckTest : IDisposable
{
    private static readonly string PreviousEpoch = PlatformCompatibility.KeyOf(3, 1);
    private static readonly string CurrentEpoch = PlatformCompatibility.KeyOf(3, 2);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-module-identity-{Guid.NewGuid():N}");

    /// <summary>A real module bundle (the shape module-pack writes) stating <paramref name="frameworkMvid"/>.</summary>
    private string Bundle(string name, string? frameworkMvid) =>
        BundleWithManifest(name, JsonSerializer.Serialize(new
        {
            plugin = name,
            version = "1.0.0",
            frameworkMvid,
            module = new { assemblyName = "MeshWeaver." + name, assemblies = new[] { $"MeshWeaver.{name}.dll" } },
        }));

    /// <summary>A bundle whose manifest entry carries <paramref name="manifestJson"/> verbatim.</summary>
    private string BundleWithManifest(string name, string manifestJson)
    {
        Directory.CreateDirectory(_root);
        var manifest = new PluginManifest(name, "MeshWeaver.Plugin." + name, "1.0.0", name, null, []);
        var path = Path.Combine(_root, $"{name.ToLowerInvariant()}.module.nupkg");
        using (var file = File.Create(path))
            NuGetPackageWriter.Write(file, manifest, "3.0.0",
                [new NuGetPackageWriter.Entry(NuGetPackageWriter.ModuleEntryPathFor($"MeshWeaver.{name}.dll"),
                    () => new MemoryStream("MZ"u8.ToArray()))],
                manifestJson);
        return path;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = ModuleIdentityCheck.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>The carried-forward set: every bundle was built for the previous epoch, so it passes.</summary>
    [Fact]
    public void ACarriedForwardSet_BuiltForTheCeiling_Passes()
    {
        var ai = Bundle("AI", PreviousEpoch);
        var maps = Bundle("Maps", PreviousEpoch);

        var (exit, output, error) = Run(ai, maps, "--expect", PreviousEpoch);

        exit.Should().Be(0, error);
        output.Should().Contain($"2 of 2 bundle(s) built for {PreviousEpoch}");
    }

    /// <summary>
    /// 🚨 The gap #3583 names: this run's module bundles are CURRENT-epoch builds. Sealed under the
    /// previous key, an old-epoch portal would adopt bytes built for a platform it does not run.
    /// One such bundle among good ones refuses the whole set, by name.
    /// </summary>
    [Fact]
    public void ABundleBuiltForTheCurrentEpoch_IsRefused_ByName()
    {
        var carried = Bundle("AI", PreviousEpoch);
        var fresh = Bundle("Maps", CurrentEpoch);

        var (exit, output, error) = Run(carried, fresh, "--expect", PreviousEpoch);

        exit.Should().Be(1);
        error.Should().Contain("REFUSED maps.module.nupkg").And.Contain(CurrentEpoch);
        output.Should().Contain($"1 of 2 bundle(s) built for {PreviousEpoch}");
    }

    /// <summary>A bundle that states nothing, or states an identity in another scheme, cannot be
    /// shown to belong to the old epoch, so it is refused rather than sealed on a guess.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("g0123456789abcdef0123456789abcdef01234567")]
    [InlineData("s0123456789abcdef0123456789abcdef")]
    public void ABundleThatCannotBeShownToMatch_IsRefused(string? stated)
    {
        var (exit, _, error) = Run(Bundle("AI", stated), "--expect", PreviousEpoch);

        exit.Should().Be(1, "only a measured match passes");
        error.Should().Contain("REFUSED ai.module.nupkg");
    }

    /// <summary>A check over nothing verifies nothing: refused as bad usage, never a pass.</summary>
    [Fact]
    public void NoBundles_IsBadUsage_NeverAPass()
    {
        Run("--expect", PreviousEpoch).Exit.Should().Be(2);
        Run(Bundle("AI", PreviousEpoch)).Exit.Should().Be(2, "no --expect");
    }

    /// <summary>
    /// An UNREADABLE bundle is exit 2 as documented — a manifest that is not JSON included, which
    /// used to escape as a <c>JsonException</c> the CLI's outer catch reported as 70.
    /// </summary>
    [Fact]
    public void AnUnreadableBundle_IsExit2_NotAnUnhandledThrow()
    {
        var malformed = BundleWithManifest("AI", "{ this is not json");
        var (exit, _, error) = Run(malformed, "--expect", PreviousEpoch);
        exit.Should().Be(2, error);
        error.Should().Contain("cannot read");

        Directory.CreateDirectory(_root);
        var notAnArchive = Path.Combine(_root, "plain.module.nupkg");
        File.WriteAllText(notAnArchive, "not a zip");
        Run(notAnArchive, "--expect", PreviousEpoch).Exit.Should().Be(2);

        Run(Path.Combine(_root, "missing.module.nupkg"), "--expect", PreviousEpoch).Exit.Should().Be(2);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
