using MeshWeaver.Blazor.Pages;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Table-test for <see cref="ApplicationPage.ShowFullProgress"/> — the pure render-branch
/// decision that determines when the whole content area is replaced by the full-page
/// progress bar. The rule: full progress ONLY when there is no previously-rendered
/// ViewModel to keep showing. Once a layout area has rendered, in-circuit navigations
/// keep it mounted (keep-last-good) and slow navigations surface only the compact
/// overlay — never the full-page "interrupt" that blanked the page on every
/// slide-to-slide navigation.
/// </summary>
public class ApplicationPageProgressDecisionTest
{
    [Theory]
    // Not interactive (prerender without cached HTML) → always full progress,
    // regardless of loading / context / ViewModel state.
    [InlineData(false, false, false, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, true, true, true, true)]
    // Interactive, NO previously-rendered ViewModel: the first resolution has nothing
    // to keep showing → full progress while loading or while the context is missing.
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, false, false, false, true)]
    // Interactive, no ViewModel, context resolved and not loading: the content branch
    // renders (the ViewModel is assigned in the same cycle the context lands).
    [InlineData(true, false, true, false, false)]
    // Interactive WITH a previously-rendered ViewModel: NEVER the full progress page —
    // keep the last-good content mounted (loading or not, context or not).
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, true, false)]
    public void ShowFullProgress_Decision(
        bool isInteractive, bool isLoading, bool hasContext, bool hasViewModel, bool expected)
    {
        ApplicationPage.ShowFullProgress(isInteractive, isLoading, hasContext, hasViewModel)
            .Should().Be(expected);
    }
}
