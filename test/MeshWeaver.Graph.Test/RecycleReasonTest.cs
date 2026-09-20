using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 An operator recycle carries the operator's own WHY — and never loses the fallback (#4782).
///
/// <para><c>AGENTS.md</c> states it as an absolute ("ALWAYS carry a <c>Reason</c>"), and the
/// <c>recycle-after-deploy</c> procedure names this verb as the way to do it — but the surface took
/// a path and nothing else, so the rule was unsatisfiable from the place a reader was sent. #3712
/// had already stopped this verb posting an ANONYMOUS teardown, so the line names who tore the hub
/// down and through which surface; what no framework string can supply is what the operator was
/// trying to fix, which is exactly the half a reader of a <c>[QUIESCE-START]</c> cannot
/// reconstruct.</para>
///
/// <para>🚨 Both directions, and the second is the one that matters more. An omitted reason must
/// still produce #3712's sentence, never a blank — a blank is the precise failure #3510 measured at
/// six occurrences and four bake seals, and "I added a parameter" is exactly the change that
/// silently turns a fallback into an empty string.</para>
/// </summary>
public class RecycleReasonTest
{
    private const string Path = "Store/Plugin";

    [Fact]
    public void TheOperatorsReason_SurvivesIntoTheDisposeRequest()
        => MeshOperations.RecycleReason(Path, "the compile pinned a stale assembly after the roll")
            .Should().Contain("the compile pinned a stale assembly after the roll");

    [Fact]
    public void TheFrameworksOwnSentence_IsKeptAlongsideIt()
        // Not REPLACED by the operator's text: who and by-which-surface stay on the line, because
        // the operator's sentence answers a different question from #3712's.
        => MeshOperations.RecycleReason(Path, "a stale assembly")
            .Should().Contain("MeshOperations.Recycle").And.Contain(Path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReason_StillProducesTheFallback_NeverABlank(string? absent)
    {
        // 🚨 THE control. Whitespace is included deliberately: a UI that sends an untouched text box
        // supplies "" or " ", and a naive `reason ?? framework` would hand the target a Reason that
        // is present and empty — which prints as nothing at all and is worse than the fallback it
        // replaced, because it looks like a caller who answered.
        var reason = MeshOperations.RecycleReason(Path, absent);

        reason.Should().Contain("MeshOperations.Recycle");
        reason.Should().Contain("an operator asked for");
        reason.Should().NotContain("The operator's reason:");
    }

    [Fact]
    public void TheReasonIsTrimmed_SoATrailingNewlineDoesNotBreakTheLine()
        => MeshOperations.RecycleReason(Path, "  a stale assembly\n")
            .Should().EndWith("The operator's reason: a stale assembly");

    // ── The reason is OPERATOR INPUT, and it lands on a log line (#4952 review) ──────────────

    [Theory]
    [InlineData("routine\nfail: MeshWeaver.Something[0] forged entry")]
    [InlineData("routine\r\nfail: forged")]
    [InlineData("routine\rfail: forged")]
    [InlineData("routine\u0085fail: forged")]
    [InlineData("routine\vfail: forged")]
    public void ALineBreakInTheReason_CannotStartASecondLogLine(string injected)
    {
        // 🚨 A FORGERY PRIMITIVE, not a formatting nit. This text is rendered into the target's
        // [QUIESCE-START] line, and the log-incident filer fingerprints on the RENDERED line — so a
        // second line here reads exactly like a real entry from another component and can mint an
        // incident. `Trim()` does not touch embedded breaks, which is what made it insufficient.
        var reason = MeshOperations.RecycleReason(Path, injected);

        reason.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u0085").And.NotContain("\v");
        reason.Should().Contain("forged",
            "the TEXT is not the problem and must survive — only its ability to break the line is removed");
    }

    [Fact]
    public void AVeryLongReason_CannotPushTheFrameworksSentenceOutOfView()
    {
        var reason = MeshOperations.RecycleReason(Path, new string('x', 5_000));

        reason.Should().Contain("MeshOperations.Recycle", "the framework's own sentence must survive");
        reason.Should().Contain("(truncated)");
        reason.Length.Should().BeLessThan(1_000);
    }
}
