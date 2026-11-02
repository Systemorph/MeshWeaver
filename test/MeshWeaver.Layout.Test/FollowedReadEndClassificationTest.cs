using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A server-side render that fails because a read reached NO VERDICT shows the localized
/// "could not check" copy, never the English diagnostic in the generic panel (review of #7832).
/// Pre-fix the cache read gate ended such a read with a denial, which had a localized frame; once it
/// ended as NO VERDICT, <c>LayoutAreaHost.CreateRenderErrorControl</c> had no branch for it.
/// </summary>
public class NoVerdictRenderTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string UndecidedView = nameof(UndecidedView);
    private const string BrokenView = nameof(BrokenView);
    private const string EngineeringFault = "BOOM_an_ordinary_defect";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(UndecidedView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(ReadVerdict.NoVerdict("user 'u' may read 'Space/Doc'", new TimeoutException("stalled"))))
                .WithView(BrokenView, (LayoutAreaHost _, RenderingContext _)
                    => Observable.Throw<UiControl?>(new InvalidOperationException(EngineeringFault))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private async Task<string> Rendered(string area)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));
        var control = await stream.GetControlStream(area)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is MarkdownControl);
        var text = (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"frame: {text}");
        return text;
    }

    [HubFact]
    public async Task ARenderThatReachedNoVerdict_ShowsTheLocalizedCouldNotCheckFrame()
    {
        var text = await Rendered(UndecidedView);

        text.Should().NotContain("Permission check unavailable", "the diagnostic is internal and English-only");
        text.Should().NotContain("TimeoutException");
        text.Should().Contain("We could not load this just now.", "the localized error.checkUnavailable copy");
    }

    [HubFact]
    public async Task NegativeControl_AnOrdinaryRenderFault_KeepsTheGenericPanelAndItsMessage()
        => (await Rendered(BrokenView)).Should().Contain(EngineeringFault);
}

/// <summary>
/// How a view classifies the end of a followed read (MeshWeaver#7825–#7830). A read on which no
/// verdict was reached is NO VERDICT — the "temporarily unavailable" state, never "Access denied";
/// a decided refusal is an expected user-side outcome — the denied state, logged at Warning, never an
/// engineering Error. Pre-fix both reached the view as an <see cref="InvalidOperationException"/>
/// carrying the denial's text, which <see cref="AreaErrorClassifier.IsExpectedUserActionFailure"/>
/// did not recognise (Error on every view) and <see cref="AreaErrorClassifier.TryGetAccessDeniedPath"/>
/// read as a denial even when nothing had been decided.
/// </summary>
public class FollowedReadEndClassificationTest
{
    [Fact]
    public void AReadEndedWithoutAVerdict_IsAnAvailabilityFailure_NeverADenial()
    {
        var end = ReadVerdict.NoVerdict("user 'u' may keep reading 'Space/Doc'", new TimeoutException("stalled"));

        Assert.True(AreaErrorClassifier.IsAvailabilityFailure(end));
        Assert.False(AreaErrorClassifier.IsAccessDenied(end));
        Assert.Null(AreaErrorClassifier.TryGetAccessDeniedPath(end));
        Assert.False(AreaErrorClassifier.IsExpectedUserActionFailure(end), "no verdict is an engineering signal, not a user action");
    }

    [Fact]
    public void ARefusedRead_IsAnExpectedUserActionFailure()
    {
        var end = new UnauthorizedAccessException(
            "Access denied: user 'u' lacks Read permission on 'Space/Doc' — the read stream s served has ended (revoked, expired, or no longer admitted)");

        Assert.True(AreaErrorClassifier.IsExpectedUserActionFailure(end));
        Assert.True(AreaErrorClassifier.IsAccessDenied(end));
        Assert.Equal("Space/Doc", AreaErrorClassifier.TryGetAccessDeniedPath(end));
    }

    [Fact]
    public void NegativeControl_TheUntypedEndThePreFixSubscriberRaised_IsLoggedAsAnError()
    {
        // What a StreamErrorEvent re-raised before it carried its kind: the denial's text in an
        // InvalidOperationException. The log-level predicate cannot see it — every view logged Error.
        var end = new InvalidOperationException(
            "Access denied: user 'u' lacks Read permission on 'Space/Doc' — the read stream s served has ended (revoked, expired, or no longer admitted)");

        Assert.False(AreaErrorClassifier.IsExpectedUserActionFailure(end));
    }
}
