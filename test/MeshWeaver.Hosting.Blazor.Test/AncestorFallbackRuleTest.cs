using System;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The nearest-existing-ancestor fallback must fire on ABSENCE and on nothing else.
///
/// <para>The failure mode worth testing is not "it doesn't fire" — that is a dead link, visible and
/// annoying. It is firing on something that is not an absence: a denied page rendered as "here is
/// its parent instead" tells a correctly-blocked user the content is gone and a wrongly-blocked one
/// nothing actionable, and an availability failure presented as absence is a fabricated negative
/// (#974: no verdict was reached at all). <c>ErrorType.Unknown</c> matters specifically because it
/// is the enum's DEFAULT and the value an unclassified <c>d.Failed(reason)</c> refusal carries —
/// exactly the refusal-that-looks-like-absence shape #1253/#1279 was about.</para>
/// </summary>
public class AncestorFallbackRuleTest
{
    private static Exception Failure(ErrorType type) =>
        new DeliveryFailureException(new DeliveryFailure(null!, "load failed") { ErrorType = type });

    // ── fires: the typed absences the navigation layer already read as "page not found" ─────

    [Theory]
    [InlineData(ErrorType.NotFound)]   // routing: no node at that address
    [InlineData(ErrorType.Ignored)]    // the hub has no handler — the area does not exist
    public void Fires_on_a_typed_absence(ErrorType type)
        => Assert.True(AncestorFallbackRule.ShouldFallBack(Failure(type), "Underwriting/X", "Y", "Underwriting/X/Y"));

    [Fact]
    public void Fires_when_the_absence_is_wrapped()
        => Assert.True(AncestorFallbackRule.ShouldFallBack(
            new InvalidOperationException("area stream", Failure(ErrorType.NotFound)),
            "Underwriting/X", "Y", "Underwriting/X/Y"));

    // ── never fires: everything that is not an absence ──────────────────────────────────────

    [Theory]
    [InlineData(ErrorType.Unauthorized)]      // not authenticated
    [InlineData(ErrorType.Forbidden)]         // authenticated, lacks permission
    [InlineData(ErrorType.Unavailable)]       // NO VERDICT — never present as absence (#974)
    [InlineData(ErrorType.Unknown)]           // the DEFAULT, and an unclassified refusal (#1253)
    [InlineData(ErrorType.Exception)]
    [InlineData(ErrorType.Rejected)]
    [InlineData(ErrorType.Failed)]
    [InlineData(ErrorType.ShuttingDown)]
    [InlineData(ErrorType.CompilationFailed)]
    [InlineData(ErrorType.CompilationInProgress)]
    public void Never_fires_on_anything_that_is_not_an_absence(ErrorType type)
        => Assert.False(AncestorFallbackRule.ShouldFallBack(Failure(type), "Underwriting/X", "Y", "Underwriting/X/Y"),
            $"{type} must keep failing with its own reason — masking it as 'here is the parent' is worse "
            + "than the dead end, because it looks like an answer");

    [Fact]
    public void Never_fires_on_a_timeout()
        => Assert.False(AncestorFallbackRule.ShouldFallBack(
            new TimeoutException("the load did not complete"), "Underwriting/X", "Y", "Underwriting/X/Y"));

    [Fact]
    public void Never_fires_on_an_untyped_exception_or_none_at_all()
    {
        Assert.False(AncestorFallbackRule.ShouldFallBack(new Exception("boom"), "A", "B", "A/B"));
        Assert.False(AncestorFallbackRule.ShouldFallBack(null, "A", "B", "A/B"));
    }

    // ── the shape limits that bound it to one hop ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Never_fires_without_a_remainder(string? remainder)
        => Assert.False(AncestorFallbackRule.ShouldFallBack(Failure(ErrorType.NotFound), "Underwriting/X", remainder, "Underwriting/X"),
            "a bare EXISTING path that fails to load is a real failure, not a wrong address — and it "
            + "is also what stops the ancestor's own load from falling back again");

    [Fact]
    public void Never_fires_when_the_ancestor_is_where_we_already_are()
        => Assert.False(AncestorFallbackRule.ShouldFallBack(
            Failure(ErrorType.NotFound), "Underwriting/X", "Y", "/Underwriting/X/"),
            "redirecting a path to itself would loop the navigation");

    [Fact]
    public void Never_fires_without_an_ancestor_to_fall_back_to()
        => Assert.False(AncestorFallbackRule.ShouldFallBack(Failure(ErrorType.NotFound), "", "Y", "Bogus/Y"));
}
