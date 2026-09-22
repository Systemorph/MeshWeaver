using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins that the <see cref="DataContext"/> init time-box names what was outstanding INSIDE a pending
/// type-source leg when the type source can say (Systemorph/MeshWeaver#1122).
///
/// <para><b>Why the leg name alone was not enough.</b> The newest attributed occurrence on #1122
/// (<c>Collaboration</c>, memex-cloud, 2026-09-21 17:06:24Z) read
/// <i>"type-source legs still outstanding: 7j8ehN2m0UCo51iBcrGL2A/MeshNode"</i>. For a per-node hub
/// that one leg is <c>MeshNodeTypeSource.Initialize</c>, which CONCATENATES an unbounded durable
/// storage read ahead of the routing-supplied own-node stream — so the pending leg did not separate
/// "a storage read never came back" from "the routing stream never delivered", the one distinction
/// the attribution was written to make. A type source that implements
/// <see cref="IReportsInitialLoadProgress"/> now has its sentence appended to its leg.</para>
///
/// <para>Negative control: with the ledger rendering keys only (the pre-change shape) the message
/// carries the leg but not <see cref="ProgressSentence"/>, and the first assertion fails.</para>
/// </summary>
public class DataContextInitTimeoutNamesTheWaitInsideTheLegTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ProgressSentence = "durable seed read of 'probe/path' outstanding — the part still waited on";

    private static TimeSpan InitBound => TestTimeouts.Quick / 8;

    private static TimeSpan AnswerBound => TestTimeouts.Quick / 2;

    private record HangingItem(string Id);

    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    /// <summary>
    /// A type source whose initial load never settles and which reports what it is waiting on —
    /// the shape of <c>MeshNodeTypeSource</c>, reduced to the two properties under test.
    /// </summary>
    private sealed record ReportingHangingTypeSource(IWorkspace Workspace, object DataSource)
        : TypeSourceWithType<HangingItem>(Workspace, DataSource), IReportsInitialLoadProgress
    {
        protected override IObservable<InstanceCollection> Initialize(
            WorkspaceReference<InstanceCollection> reference, CancellationToken cancellationToken)
            => Observable.Never<InstanceCollection>();

        public string DescribeInitialLoadProgress() => ProgressSentence;
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                .WithInitializationTimeout(InitBound)
                .AddSource(src => src
                    .WithType<HangingItem>(t =>
                    {
                        var typed = (TypeSourceWithType<HangingItem>)t;
                        return new ReportingHangingTypeSource(typed.Workspace, typed.DataSource);
                    }),
                    "reporting-source"));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 120_000)]
    public async Task TimedOutInit_AppendsWhatTheLegItselfSaysItIsWaitingOn()
    {
        var host = GetHost();
        var client = GetClient();

        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(AnswerBound).Await(TestContext.Current.CancellationToken);
        var ex = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init hung must answer with the failed-state rejection")).Which;
        ex.Should().NotBeOfType<TimeoutException>(
            "a TimeoutException is the probe's own wait expiring behind a shut gate — the wedge");

        var message = host.GetWorkspace().DataContext.InitializationError!.Message;

        message.Should().Contain($"/{nameof(HangingItem)} [{ProgressSentence}]",
            "the pending leg must carry what the type source says is outstanding inside it — for a "
            + "per-node hub that is what separates a storage read that never returned from a routing "
            + "stream that never delivered");
    }
}
