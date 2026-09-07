using System;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The upsert's CREATE leg bounds itself STRICTLY ABOVE the framework's own write verdict, so that
/// when a write goes unanswered the framework's terminal wins and NAMES the cause
/// (<c>OwnerUnreachable — the owner produced no terminal for this patch</c>) instead of this leg's
/// generic refusal firing first and hiding it.
///
/// <para>That ordering is <see cref="LatePatchResponseRegistry.WriteVerdictBound"/>'s own stated
/// requirement — <i>"Anything waiting on a write must bound itself STRICTLY ABOVE this, so the
/// framework's terminal wins and names the cause"</i> — and getting it wrong is #2819: an assertion
/// that reported "the observable emitted nothing at all" one second before the write would have
/// explained itself. The failure carrying the explanation was never the one anybody read.</para>
///
/// <para>🚨 This is a pure ordering invariant, deliberately with no clock in it. The behaviour it
/// protects (#3510's create leg answering rather than hanging) can only be exercised by letting a
/// real bound elapse, which would cost a ~36 s test AND a hand-written timeout literal that
/// <c>TestTimeoutLiteralRatchetGuard</c> refuses. What is cheap to pin is the part that was
/// actually got wrong once: the direction of the inequality.</para>
/// </summary>
public class InnerCreateBoundOutlivesTheFrameworkVerdictTest
{
    [Fact]
    public void TheCreateLegOutwaitsTheFrameworksOwnVerdict()
    {
        Assert.True(
            MeshExtensions.InnerCreateVerdictBound > LatePatchResponseRegistry.WriteVerdictBound,
            $"the create leg bounds itself at {MeshExtensions.InnerCreateVerdictBound}, which is not "
            + $"strictly above the framework's own {LatePatchResponseRegistry.WriteVerdictBound}. A "
            + "caller waiting on a write must let the framework's terminal land first — bounding at "
            + "or below WriteVerdictBound reproduces #2819, where the refusal that names the cause "
            + "arrives after the one that does not.");
    }

    [Fact]
    public void TheMarginIsNotSoLargeThatItStopsBeingABound()
    {
        // Above the framework's verdict, but far below any caller budget it is meant to protect
        // (the installer's is 10 minutes). A bound that approaches the caller's budget answers
        // nothing the caller had not already given up on.
        var margin = MeshExtensions.InnerCreateVerdictBound
            - LatePatchResponseRegistry.WriteVerdictBound;
        Assert.True(margin < TimeSpan.FromMinutes(1),
            $"the margin over the framework's verdict is {margin}; a bound that approaches the "
            + "caller's own budget answers nothing the caller had not already given up on.");
    }
}
