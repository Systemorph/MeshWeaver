#pragma warning disable CS1591

using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>#3510 — a teardown that cannot accomplish anything must not happen.</b>
///
/// <para><c>PackageInstaller</c> recycles a package's root after an install so the hub that comes
/// back binds the package's OWN configuration rather than the fallback. That is worth a teardown
/// only when the root's in-package NodeType was actually rebuilt — and the two
/// <c>RequestReleases</c> waves the recycle sits between are both gated on
/// <c>result.Written &gt; 0</c> for exactly that reason. The recycle alone ran unconditionally.</para>
///
/// <para><b>Measured on CD run 34190841613 (2026-09-08):</b> package <c>Video</c> logged
/// <c>0 written, 21 unchanged</c> and recycled its root anyway; <c>Chess</c> was recycled TWICE per
/// bake. Each teardown killed the correlated work in flight beneath the root — its own
/// <c>PluginGating</c> reconcile — which the quiesce could not answer and force-cancelled at its
/// 2 s bound, surfacing to the issuer as <c>HubDisposedBeforeResponseException</c>.</para>
///
/// <para>🚨 <b>What this does NOT claim.</b> It does not fix the case where an install DID write.
/// A recycle with a real job still tears down a root whose background pipeline is issuing
/// correlated requests, and a hub that keeps accepting work while quiescing is its own defect.
/// This removes only the class where the teardown was provably pointless, and it removes it by
/// making the recycle agree with its neighbours rather than by widening any bound.</para>
///
/// <para>🚨 It lives HERE, next to the self-update suites, rather than beside the installer: the
/// rule is an <c>internal</c> of <c>MeshWeaver.PluginCatalog</c>, and this is the only test project
/// in THIS repository that both references that assembly and appears in its
/// <c>InternalsVisibleTo</c> list (<c>MeshWeaver.PluginCatalog.Test</c> lives in
/// MeshWeaver.Plugins). Moving the assembly's tests is a bigger change than this fix.</para>
/// </summary>
public class RetypedRootRecycleNeedsAJobTest
{
    /// <summary>
    /// 🚨 THE REGRESSION CASE. A re-install that changed nothing rebuilt nothing, so the recycle
    /// has nothing to rebind to — and the sentence has to SAY that, because a silent skip and a
    /// skip nobody chose read identically in a log.
    /// </summary>
    [Fact]
    public void AnInstallThatWroteNothing_DoesNotRecycleAndSaysWhy()
    {
        var reason = PackageInstaller.RecycleDeclineReason("Video", written: 0);

        Assert.NotNull(reason);
        Assert.Contains("wrote nothing", reason);
        Assert.Contains("not rebuilt", reason);
        Assert.Contains("cancel the work in flight", reason);
    }

    /// <summary>
    /// 🚨 THE CONTROL. An install that wrote SOMETHING did rebuild the in-package type, and the
    /// recycle is the whole point of the ordering it sits in (#1732). Without this case a
    /// predicate that declined every recycle would satisfy the test above while silently
    /// re-breaking the binding the recycle exists to fix.
    /// </summary>
    [Fact]
    public void AnInstallThatWroteSomething_StillRecycles()
        => Assert.Null(PackageInstaller.RecycleDeclineReason("Video", written: 1));

    /// <summary>
    /// No retyped root means the placeholder dance never ran, so there is no binding to replace
    /// and the caller returns before it would log anything. Null — never a decline SENTENCE, which
    /// would report a skip that is not a decision.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoRetypedRoot_IsNotADecline(string? rootPath)
    {
        Assert.Null(PackageInstaller.RecycleDeclineReason(rootPath, written: 0));
        Assert.Null(PackageInstaller.RecycleDeclineReason(rootPath, written: 5));
    }

    /// <summary>
    /// 🚨 <b>The recycle that DOES happen must announce itself IN THE VICTIM'S OWN LOG (#3510).</b>
    ///
    /// <para>The decline above is announced by the installer, to whoever is reading the install.
    /// The recycle is read by somebody else entirely — whoever is looking at the root's
    /// <c>[QUIESCE-START]</c>, or at one of the per-node children the cascade takes with it, which
    /// is where #3510's stranded writes were owed. That reader had nothing: attributing CD 7950's
    /// <c>Hosting</c> took a full read of <c>PackageInstaller</c> plus an ordering argument, because
    /// every candidate recycler announces itself at Information and none of them announced itself
    /// where the teardown was visible.</para>
    ///
    /// <para>The sentence must name the DISCRIMINATOR the issue settled on, verbatim: <i>"a root
    /// recycling under a reconcile is usually benign … the discriminator is not the count — it is
    /// whether the recycled root is the package currently installing"</i>. A reason saying only
    /// "recycling a root" would satisfy a weaker test and leave the next reader exactly where #3510
    /// left them.</para>
    /// </summary>
    [Fact]
    public void TheRecycleThatProceeds_NamesTheInstallItIsRunningUnder()
    {
        var reason = PackageInstaller.RetypedRootRecycleReason("Hosting");

        Assert.Contains("Hosting", reason);
        Assert.Contains("SettleRetypedRoot", reason);
        Assert.Contains("WHILE INSTALLING", reason);
    }
}
