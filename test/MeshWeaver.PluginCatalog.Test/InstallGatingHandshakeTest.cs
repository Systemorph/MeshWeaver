#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The handshake between the installer and a gating node type.
///
/// <para>🚨 <b>Why this is a test and not a constant nobody checks.</b> An install used to return
/// as soon as the root NODE could be read, but the gating pass that seeds the cover grants runs as
/// a CONSEQUENCE of that activation, asynchronously. So the installer reported a clean install over
/// a partition that still denied every viewer — measured at 12–17 s, and the reason
/// MeshWeaver.Education's disposable-mesh e2e fails non-deterministically with
/// <c>Access denied: user 'e2e-admin' lacks Read permission on '{course}'</c> followed by a 180 s
/// coupon timeout.</para>
///
/// <para>The installer now WATCHES for that grant on every partition it deliberately leaves GATED.
/// That makes the grant's PATH a contract between core (which watches it) and the plugin (which
/// writes it) — and a contract nothing checks is one that drifts. If <c>PluginGate</c> ever renames
/// the cover grant, the watch would not fail loudly; every gated install would spend its detector
/// budget and report a stall that is really a rename. Hence this pin. The detector's own three
/// outcomes are pinned in <see cref="GatingDetectorTest"/>.</para>
/// </summary>
public class InstallGatingHandshakeTest
{
    // The literal PluginGate produces — see Store/Plugin/Test/PluginGateTests in
    // MeshWeaver.Plugins: `PluginGate.ViewerAssignment("MyPlug", "Public", denied: false).Path`
    // is asserted there to equal "MyPlug/_Access/Public_Access".
    private const string PluginGateCoverGrant = "MyPlug/_Access/Public_Access";

    [Fact]
    public void The_installer_waits_on_the_path_the_gate_actually_writes() =>
        Assert.Equal(PluginGateCoverGrant, PackageInstaller.CoverGrantPath("MyPlug"));

    [Theory]
    [InlineData("Edu")]
    [InlineData("ThinkInStreams")]
    [InlineData("Nested/Package")]
    public void The_grant_is_a_satellite_of_the_partition_root(string partition)
    {
        var path = PackageInstaller.CoverGrantPath(partition);

        // A satellite of THIS root — not a sibling, and not a path that would silently resolve
        // somewhere else for a nested partition.
        Assert.StartsWith(partition + "/", path);
        Assert.Contains("/_Access/", path);
    }
}
