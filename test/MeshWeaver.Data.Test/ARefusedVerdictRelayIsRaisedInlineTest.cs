using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>An item for <see cref="ARefusedVerdictRelayIsRaisedInlineTest"/>.</summary>
/// <param name="Id">The key.</param>
public record VerdictRelayItem(string Id);

/// <summary>
/// 🚨 #7492 follow-up: <b>a subscribe verdict the stream's own hub REFUSES is still raised.</b>
///
/// <para><c>JsonSynchronizationStream.RaiseVerdictOnStreamHub</c> hands the owner's verdict to the
/// stream's <c>sync/{id}</c> hub instead of running readers on the subscribing hub's turn. When that
/// hub is already in teardown, its <c>Post</c> answers with a <c>Failed</c> envelope having enqueued
/// nothing. The relay read any non-null envelope as "carried" and returned, so the stream never
/// received its terminal and every reader of it waited forever. The decider must be the intake
/// verdict (<c>WasAcceptedForDelivery</c>), with the inline <c>OnError</c> as the fallback.</para>
///
/// <para><b>The state.</b> The race in production is the hub crossing into teardown between any
/// check and the post. The test holds that state deterministically instead of racing for it: the
/// stream's own registrant PARKS while its hub runs its <c>ShutDown</c> phase — past
/// <c>DisposeHostedHubs</c>, so the hub refuses every post, yet before the hub reference is
/// released and before the stream is disposed, so the stream can still take a terminal. The park
/// is the subject: a volatile flag polled under a bounded <see cref="SpinWait"/>, released in
/// <c>finally</c>.</para>
///
/// <para><b>Negative control.</b> With the acceptance check reverted to <c>is not null</c>, the
/// reader's error never arrives and the <c>Emit</c> below fails.</para>
/// </summary>
public class ARefusedVerdictRelayIsRaisedInlineTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source => source
                .WithType<VerdictRelayItem>(type => type
                    .WithKey(i => i.Id)
                    .WithInitialData(_ => Observable.Return(new[] { new VerdictRelayItem("one") }.AsEnumerable())))));

    /// <summary>
    /// A verdict refused by the stream's tearing-down hub reaches the stream's readers inline.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AVerdictTheStreamHubRefuses_IsStillRaisedOnTheStream()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var collection = workspace.DataContext.GetTypeSource(typeof(VerdictRelayItem))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collection))!;
        var streamHub = stream.Hub;

        var parked = new AsyncSubject<Unit>();
        var release = 0;
        stream.RegisterForDisposal(Disposable.Create(() =>
        {
            parked.OnNext(Unit.Default);
            parked.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
        }));

        try
        {
            streamHub.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the stream hub's ShutDown phase must reach the stream's registrants",
                    cancellationToken: TestContext.Current.CancellationToken);

            // The precondition that makes this test able to fail: the hub refuses posts now, and
            // the stream still holds it.
            ((int)streamHub.RunLevel).Should().BeGreaterThanOrEqualTo((int)MessageHubRunLevel.DisposeHostedHubs,
                "the hub must already refuse posts, or the inline fallback is never exercised");
            stream.HubIfHeld().Should().BeSameAs(streamHub);

            var readerError = new AsyncSubject<Exception>();
            using var reader = stream.Subscribe(_ => { }, ex =>
            {
                readerError.OnNext(ex);
                readerError.OnCompleted();
            });

            var request = new MessageDelivery<PingRequest>(
                new PingRequest(), new PostOptions(host.Address), host.JsonSerializerOptions);
            var verdict = new DeliveryFailureException(
                new DeliveryFailure(request, "No node found") { ErrorType = ErrorType.NotFound });

            JsonSynchronizationStream.RaiseVerdictOnStreamHub(stream, verdict, NullLogger.Instance);

            var raised = await readerError.Should().Within(TestTimeouts.Quick)
                .Emit("a verdict the stream's own hub refused must be raised inline, never dropped",
                    cancellationToken: TestContext.Current.CancellationToken);
            raised.Should().BeSameAs(verdict);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }
}
