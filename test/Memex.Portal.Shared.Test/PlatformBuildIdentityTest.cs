using System.Reflection;
using MeshWeaver.Mesh;
using Xunit;

// The class and its namespace share the name "ServiceDefaults", so the bare name binds to the
// namespace here — same aliasing as VersionEndpointTest.
using Defaults = Memex.Portal.ServiceDefaults.ServiceDefaults;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The portal must report the build it is actually running, and every surface that reports it must
/// report the SAME one.
///
/// <para><b>The incident these tests pin.</b> Until 2026-09-01 the About page and the self-updater
/// read <c>Assembly.GetEntryAssembly()</c> with no fallback, while <c>/api/version</c> read the
/// same two stamps through a selection that falls back to a stamped assembly when the entry
/// assembly is not part of this build. That difference was invisible while the portal executable
/// lived in this repo and was stamped. When the portal hosts moved to MeshWeaver.Plugins
/// (2026-08-25) the executable stopped being stamped, and the two readers diverged on the deployed
/// portal: <c>/api/version</c> answered <c>3.0.0-rc9+0a1eabdc…</c> while the About page said
/// <c>Version: 1.0.0</c> and <c>Build commit: not recorded for this build</c>.</para>
///
/// <para>A version pinned at <c>1.0.0</c> is not a cosmetic defect: <c>VersionSelect.IsNewer</c>
/// then reports EVERY registry tag as newer, so the install can never reach "up to date", the
/// header build chip is permanently in its update-available state (where a click is a page reload
/// rather than a link to About), and the deployment re-rolls on every check floor.</para>
///
/// <para>🚨 <b>This test run IS the failure case</b>, which is what gives the assertions teeth:
/// <c>test/Directory.Build.props</c> deliberately does not import the root props, so
/// <c>AddCommitHashMetadata</c> never runs for a test project and the entry assembly here — the
/// runner — carries no <c>CommitHash</c>, exactly like the relocated portal executable. Each test
/// states its own non-vacuity guard rather than assuming that.</para>
/// </summary>
public class PlatformBuildIdentityTest
{
    /// <summary>An assembly THIS build stamped — the platform half of the selection.</summary>
    private static Assembly StampedAssembly => typeof(ShippedReleaseSeed).Assembly;

    /// <summary>An assembly built outside this repo, so it carries no <c>CommitHash</c>.</summary>
    private static Assembly UnstampedAssembly => typeof(object).Assembly;

    private static string? CommitOf(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "CommitHash", StringComparison.OrdinalIgnoreCase))
            ?.Value is { Length: > 0 } sha
            ? sha
            : null;

    /// <summary>
    /// A stamped entry assembly IS the build identity — the production branch. A portal executable
    /// built in this repo must keep answering for itself; the fallback exists for the other case
    /// and must never override a real answer.
    /// </summary>
    [Fact]
    public void Select_keeps_the_entry_assembly_when_this_build_stamped_it()
    {
        // Non-vacuity: the assembly handed in really is stamped, so "it was kept" means something.
        CommitOf(StampedAssembly).Should().NotBeNull(
            "AddCommitHashMetadata stamps every assembly built from this repo");

        PlatformBuildInfo.SelectBuildAssembly(StampedAssembly).Should().BeSameAs(StampedAssembly);
    }

    /// <summary>
    /// An entry assembly that is not part of this build is REFUSED as the build identity — the case
    /// a relocated portal executable and a test runner have in common. Reading it would report the
    /// SDK default <c>1.0.0</c> and no commit at all.
    /// </summary>
    [Fact]
    public void Select_falls_back_when_the_entry_assembly_is_not_part_of_this_build()
    {
        // Non-vacuity: the assembly handed in really is unstamped, so the fallback really is taken.
        CommitOf(UnstampedAssembly).Should().BeNull(
            "the framework's own assembly is built outside this repo");

        var selected = PlatformBuildInfo.SelectBuildAssembly(UnstampedAssembly);

        selected.Should().NotBeSameAs(UnstampedAssembly);
        CommitOf(selected).Should().NotBeNull("the fallback must land on an assembly this build stamped");
    }

    /// <summary>
    /// An absent entry assembly (a host that reports none) is the same case as an unstamped one.
    /// </summary>
    [Fact]
    public void Select_falls_back_when_there_is_no_entry_assembly()
        => CommitOf(PlatformBuildInfo.SelectBuildAssembly(null)).Should().NotBeNull();

    /// <summary>
    /// The About page's build identity is the REAL build, not the host's default — asserted on the
    /// commit because that half is independent of the injected run number, so the assertion holds
    /// whether or not a container variable is present in this process.
    /// </summary>
    [Fact]
    public void The_about_page_reports_a_real_build_commit_under_an_unstamped_host()
    {
        // Non-vacuity: this process's entry assembly is the unstamped shape the fix is about.
        CommitOf(Assembly.GetEntryAssembly()!).Should().BeNull(
            "test projects do not import the root props, so the runner carries no CommitHash");

        ShippedReleaseSeed.CommitHash.Should().NotBeNull(
            "reading only the entry assembly reports 'not recorded for this build' on every " +
            "deployment whose executable is built outside this repo");
    }

    /// <summary>
    /// 🚨 The whole point: <c>/api/version</c> and the About page CANNOT disagree. They are separate
    /// readers only because Memex.Portal.Shared sits far above the infrastructure project — they
    /// must resolve the same assembly and read the same two stamps, and this is the assertion that
    /// makes that structural rather than a coincidence of how each was written.
    /// </summary>
    [Fact]
    public void The_about_page_and_the_version_endpoint_report_the_same_build()
    {
        // Non-vacuity, and a real assertion rather than a skip: with a run number injected the two
        // surfaces legitimately differ (the endpoint reports assembly metadata only), so a host
        // that sets the variable must fail here loudly instead of quietly proving nothing.
        PlatformBuildInfo.RuntimePlatformVersion.Should().BeNull(
            "no container variable is injected in a test process — see ShippedReleaseSeed");

        ShippedReleaseSeed.InstalledPlatformVersion.Should().Be(Defaults.Build.Version,
            "the deployed portal answered 3.0.0-rc9 on /api/version and 1.0.0 on the About page");

        (ShippedReleaseSeed.CommitHash ?? "").Should().Be(Defaults.Build.Commit,
            "one build produced this process — the two surfaces name the same commit or one is lying");
    }
}
