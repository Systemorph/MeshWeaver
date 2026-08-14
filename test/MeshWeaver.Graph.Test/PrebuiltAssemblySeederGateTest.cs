using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the one decision that makes adopting a prebuilt assembly safe: <b>an assembly is adopted
/// only when it was built against THIS framework's content.</b>
///
/// <para><b>Why this is guarded and not merely documented.</b> <c>FrameworkVersion</c> is
/// MeshWeaver.Graph's MVID — a content identity — and the assembly-store key carries its first
/// eight characters. So seeding bytes built against a different framework writes them under the
/// LIVE framework's tag, where <c>TryGetAssemblyPath</c> reports them as a usable build and
/// <c>HasUsableBuild</c> stops the compile that was needed. The mismatch then surfaces as a
/// <c>TypeLoadException</c> inside a collectible ALC at activation: no compile error, no overlay,
/// nothing to grep. A too-permissive gate here does not degrade to "recompiles more than
/// necessary" — it degrades to a portal that will not come up, for a reason nothing reports.</para>
///
/// <para>Declining, by contrast, costs one compile — exactly what happens today.</para>
/// </summary>
public class PrebuiltAssemblySeederGateTest
{
    [Fact]
    public void AbsentIdentityDeclines()
    {
        // A producer that predates MVID recording emits no identity. "It came from our CI" is not
        // evidence of ABI compatibility, so absence must decline rather than default to trust.
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(null));
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(string.Empty));
    }

    [Fact]
    public void DifferentIdentityDeclines()
    {
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason("00000000000000000000000000000000"));
    }

    [Fact]
    public void MatchingIdentityIsAdopted()
    {
        // The live value, so the test cannot pass by matching a hard-coded constant that has since
        // moved — this repo's whole framework identity changes whenever Graph's content does.
        Assert.Null(PrebuiltAssemblySeeder.DeclineReason(NodeTypeCompilationHelpers.FrameworkVersion));
    }

    [Fact]
    public void ComparisonIsExactNotCaseInsensitiveOrPrefix()
    {
        var live = NodeTypeCompilationHelpers.FrameworkVersion;

        // The store's FrameworkTag is FrameworkVersion[..8]. A prefix match here would adopt any
        // assembly whose framework merely shares that tag — which is precisely the collision the
        // full MVID exists to rule out.
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(live[..8]));
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(live.ToUpperInvariant()));
    }

    [Fact]
    public void DeclineReasonNamesBothIdentities()
    {
        // The reason is the only breadcrumb when a package silently keeps recompiling, so it has to
        // carry what was built against AND what is live — "declined" alone sends the next person
        // looking at the compiler.
        var reason = PrebuiltAssemblySeeder.DeclineReason("deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.NotNull(reason);
        Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", reason);
        Assert.Contains(NodeTypeCompilationHelpers.FrameworkVersion, reason);
    }
}
