using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 ONE definition of "is this repo a mounted path or something to clone"
/// (MeshWeaver.Plugins#1563).
///
/// <para><c>ConfiguredPackageSource.LocalCheckout</c> has always been computed from a private
/// predicate, so a consumer that needed the same answer had to mirror it — MeshWeaver.Plugins
/// carries <c>RegistryPackages.IsMountedPath</c>, written against a rule it cannot see change. Two
/// definitions of this split fail in both directions: a mount misread as a URL is polled
/// anonymously and 404s; a URL misread as a mount is read off a filesystem path that does not
/// exist.</para>
///
/// <para>Exposing it makes the mirror deletable. These cases are the contract that makes it safe to
/// delete — they are what a consumer is entitled to rely on.</para>
/// </summary>
public class LocalCheckoutPredicateTest
{
    [Theory]
    [InlineData("/data/repos/plugins")]
    [InlineData("./relative/checkout")]
    [InlineData("C:\\repos\\plugins")]
    [InlineData("~/code/MeshWeaver.Plugins")]
    public void APath_IsALocalCheckout(string repo) =>
        Assert.True(PackageSources.IsLocalCheckout(repo));

    [Theory]
    [InlineData("https://github.com/Systemorph/MeshWeaver.Plugins")]
    [InlineData("http://internal.example/repo.git")]
    [InlineData("git@github.com:Systemorph/MeshWeaver.Plugins.git")]
    public void ARemote_IsNot(string repo) =>
        Assert.False(PackageSources.IsLocalCheckout(repo));

    /// <summary>
    /// An ABSENT repo is not a local checkout. Answering true would make an unconfigured source
    /// look mounted — and a source believed mounted is not polled at all, so the failure would be
    /// a silently empty feed rather than an error.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentRepo_IsNot(string? repo) =>
        Assert.False(PackageSources.IsLocalCheckout(repo));

    /// <summary>Case does not rescue a URL, and surrounding whitespace does not create one.</summary>
    [Fact]
    public void TheSplitIsNotDefeatedByCasingOrWhitespace()
    {
        Assert.False(PackageSources.IsLocalCheckout("HTTPS://github.com/x/y"));
        Assert.False(PackageSources.IsLocalCheckout("  https://github.com/x/y  "));
        Assert.True(PackageSources.IsLocalCheckout("  /data/repos/plugins  "));
    }
}
