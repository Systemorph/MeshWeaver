using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A STREAMED response (<see cref="MessageHubExtensions.ObserveStreamed"/>): a handler answers with
/// any number of parts and then one terminal reply, and the caller receives the parts in order, then
/// the reply. Built for the model relay (a token stream coming back from a device over a participant
/// connection), where the transport may reorder deliveries and a long answer must not be cut by a
/// total-duration bound.
/// </summary>
public class StreamedResponseTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record CountRequest(int Parts, bool Reversed = false, bool GoSilent = false, bool OverCount = false, string? RawCount = null, string? RawSequence = null) : IRequest<CountDone>;

    /// <summary>Parts paced in time: part i is sent at <c>Offsets[i]</c> ms, the reply at <c>ReplyAt</c> ms.</summary>
    private record PacedRequest(ImmutableArray<int> Offsets, int ReplyAt) : IRequest<CountDone>;

    private record CountPart(int Index);

    /// <summary>Answers with the identity the request ARRIVED with.</summary>
    private record WhoAsks : IRequest<CountDone>;

    private record CountDone(string Mode);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        configuration
            .WithTypes(typeof(CountRequest), typeof(PacedRequest), typeof(WhoAsks), typeof(CountPart), typeof(CountDone))
            .WithHandler<WhoAsks>((hub, request) =>
            {
                hub.PostFinalResponse(request, new CountDone(request.AccessContext?.ObjectId ?? "(none)"), 0);
                return request.Processed();
            })
            .WithHandler<PacedRequest>((hub, request) =>
            {
                var ask = request.Message;
                for (var i = 0; i < ask.Offsets.Length; i++)
                {
                    var n = i;
                    Observable.Timer(TimeSpan.FromMilliseconds(ask.Offsets[n]))
                        .Subscribe(_ => hub.PostResponsePart(request, new CountPart(n), n));
                }
                Observable.Timer(TimeSpan.FromMilliseconds(ask.ReplyAt))
                    .Subscribe(_ => hub.PostFinalResponse(request, new CountDone("paced"), ask.Offsets.Length));
                return request.Processed();
            })
            .WithHandler<CountRequest>((hub, request) =>
            {
                var ask = request.Message;
                if (!request.AcceptsResponseParts())
                {
                    // The caller awaits ONE reply: never send it a part it would read as the reply.
                    hub.Post(new CountDone("single"), o => o.ResponseFor(request));
                    return request.Processed();
                }
                if (ask.RawSequence is { } rawSequence)
                {
                    hub.Post(new CountPart(0), o => o.ResponseFor(request).WithProperty(PostOptions.ResponsePart, rawSequence));
                    hub.PostFinalResponse(request, new CountDone("streamed"), 1);
                    return request.Processed();
                }
                var order = Enumerable.Range(0, ask.Parts);
                if (ask.Reversed)
                    order = order.Reverse();   // what an unordered transport may do
                foreach (var i in order)
                    hub.PostResponsePart(request, new CountPart(i), i);
                if (ask.RawCount is { } raw)
                    hub.Post(new CountDone("streamed"), o => o.ResponseFor(request).WithProperty(PostOptions.ResponsePartCount, raw));
                else if (!ask.GoSilent)
                    // OverCount: the reply counts one part more than was sent — a part still in flight.
                    hub.PostFinalResponse(request, new CountDone("streamed"), ask.OverCount ? ask.Parts + 1 : ask.Parts);
                return request.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(CountRequest), typeof(PacedRequest), typeof(WhoAsks), typeof(CountPart), typeof(CountDone));

    [Fact]
    public async Task Parts_arrive_in_order_then_the_reply_then_completion()
    {
        var client = GetClient();

        var all = await client.ObserveStreamed(new CountRequest(5), o => o.WithTarget(CreateHostAddress()))
            .ToList()
            .Should().Within(10.Seconds()).Emit();

        all.Should().HaveCount(6);
        all.Take(5).Should().OnlyContain(d => d.IsResponsePart());
        all.Take(5).Select(d => ((CountPart)d.Message).Index).Should().Equal(0, 1, 2, 3, 4);
        all[5].IsResponsePart().Should().BeFalse();
        ((CountDone)all[5].Message).Mode.Should().Be("streamed");
    }

    [Fact]
    public async Task Parts_posted_out_of_order_are_reordered_by_their_number()
    {
        var client = GetClient();

        var all = await client.ObserveStreamed(new CountRequest(4, Reversed: true), o => o.WithTarget(CreateHostAddress()))
            .ToList()
            .Should().Within(10.Seconds()).Emit();

        all.Where(d => d.IsResponsePart()).Select(d => ((CountPart)d.Message).Index)
            .Should().Equal([0, 1, 2, 3], "the caller reads parts by their number, not by arrival");
        all.Last().Message.Should().BeOfType<CountDone>();
    }

    /// <summary>Negative control: a plain single-reply wait is unchanged — the handler sees no
    /// AcceptsResponseParts stamp and answers once.</summary>
    [Fact]
    public async Task A_single_reply_caller_is_answered_once_and_never_sent_a_part()
    {
        var client = GetClient();

        var reply = await client.Observe(new CountRequest(3), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        ((CountDone)reply.Message).Mode.Should().Be("single");
    }

    /// <summary>A stream that stops before its reply ends in a TimeoutException after the hub's
    /// silence bound — it never hangs, and the parts that did arrive were delivered first.</summary>
    [Fact]
    public async Task A_stream_that_goes_silent_before_its_reply_times_out()
    {
        var client = GetClient(c => ConfigureClient(c)
            .WithPostingIdentity(PostingIdentity.System)
            .WithRequestTimeout(TimeSpan.FromSeconds(2)));

        var seen = await client.ObserveStreamed(new CountRequest(2, GoSilent: true), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .ToList()
            .Should().Within(15.Seconds()).Emit();

        seen.Where(n => n.Kind == System.Reactive.NotificationKind.OnNext).Should().HaveCount(2);
        seen.Last().Kind.Should().Be(System.Reactive.NotificationKind.OnError);
        seen.Last().Exception.Should().BeOfType<TimeoutException>();
    }

    /// <summary>
    /// The reply arrived but a part it counts did not: the stream stays open — and when the awaiting
    /// hub is disposed then, the stream ends with that disposal, not with a later, misleading timeout
    /// (the reply subject is already complete, so the error must travel on the parts).
    /// </summary>
    [Fact]
    public async Task Disposal_after_the_reply_but_before_a_counted_part_ends_the_stream_with_the_disposal()
    {
        var client = GetClient(c => ConfigureClient(c)
            .WithPostingIdentity(PostingIdentity.System)
            .WithRequestTimeout(TimeSpan.FromSeconds(60)));
        var seen = new System.Reactive.Subjects.ReplaySubject<System.Reactive.Notification<IMessageDelivery>>();
        using var subscription = client.ObserveStreamed(new CountRequest(1, OverCount: true), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .Subscribe(seen);

        await seen.Where(n => n.Kind == System.Reactive.NotificationKind.OnNext)
            .Should().Within(10.Seconds()).Emit("the one part that was sent arrives");
        await seen.Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
            .Should().NotEmit(1.Seconds(), "the reply counts a part that never came, so the stream stays open");

        client.Dispose();

        var end = await seen.Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
            .Should().Within(10.Seconds()).Emit("disposal ends the stream long before its 60 s silence bound");
        end.Kind.Should().Be(System.Reactive.NotificationKind.OnError);
        end.Exception.Should().BeOfType<HubDisposedBeforeResponseException>();
    }

    private IMessageHub ClientWithSilenceBound(TimeSpan bound) => GetClient(c => ConfigureClient(c)
        .WithPostingIdentity(PostingIdentity.System)
        .WithRequestTimeout(bound));

    /// <summary>
    /// The bound is SILENCE, not duration: five parts 800 ms apart (3.2 s in all) against a 2 s bound
    /// all arrive, then the reply, then completion.
    /// </summary>
    [Fact]
    public async Task A_stream_longer_than_the_bound_survives_while_parts_keep_arriving()
    {
        var client = ClientWithSilenceBound(TimeSpan.FromSeconds(2));

        var all = await client.ObserveStreamed(new PacedRequest([0, 800, 1600, 2400, 3200], 3600), o => o.WithTarget(CreateHostAddress()))
            .ToList()
            .Should().Within(15.Seconds()).Emit();

        all.Where(d => d.IsResponsePart()).Select(d => ((CountPart)d.Message).Index).Should().Equal(0, 1, 2, 3, 4);
        all.Last().Message.Should().BeOfType<CountDone>();
    }

    /// <summary>
    /// Arrivals that emit NOTHING still restart the silence: part 0 at 0 s; part 2 (out of order) at 1.5 s and
    /// the reply at 3.0 s are both buffered behind part 1, which arrives at 4.5 s. Every gap is 1.5 s under a
    /// 2 s bound, while 4.5 s pass between ordered emissions — so the buffered arrivals (the reply included)
    /// must restart the clock, and every part must precede the reply.
    /// </summary>
    [Fact]
    public async Task Buffered_arrivals_and_a_buffered_reply_restart_the_silence_bound()
    {
        var client = ClientWithSilenceBound(TimeSpan.FromSeconds(2));

        var all = await client.ObserveStreamed(new PacedRequest([0, 4500, 1500], 3000), o => o.WithTarget(CreateHostAddress()))
            .ToList()
            .Should().Within(20.Seconds()).Emit();

        all.Take(3).Select(d => ((CountPart)d.Message).Index).Should().Equal(0, 1, 2);
        all.Should().HaveCount(4);
        all[3].Message.Should().BeOfType<CountDone>();
    }

    /// <summary>A reply whose part count is present but unreadable faults the stream — it is never read as "no parts".</summary>
    [Fact]
    public async Task An_invalid_part_count_faults_the_stream_instead_of_completing_it()
    {
        var client = GetClient();

        var seen = await client.ObserveStreamed(new CountRequest(0, RawCount: "-1"), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .ToList()
            .Should().Within(10.Seconds()).Emit();

        seen.Last().Kind.Should().Be(System.Reactive.NotificationKind.OnError);
        seen.Last().Exception!.Message.Should().Contain(PostOptions.ResponsePartCount);
    }

    /// <summary>A part whose number is unreadable faults the stream at once — never a wait for a part that cannot be placed.</summary>
    [Fact]
    public async Task An_invalid_part_number_faults_the_stream_at_once()
    {
        var client = ClientWithSilenceBound(TimeSpan.FromSeconds(30));

        var seen = await client.ObserveStreamed(new CountRequest(0, RawSequence: "abc"), o => o.WithTarget(CreateHostAddress()))
            .Materialize()
            .ToList()
            .Should().Within(10.Seconds()).Emit("well inside the 30 s silence bound");

        seen.Last().Kind.Should().Be(System.Reactive.NotificationKind.OnError);
        seen.Last().Exception!.Message.Should().Contain(PostOptions.ResponsePart);
    }

    /// <summary>
    /// Several streams answered at once between the same two hubs are counted per REQUEST by the storm
    /// breaker: three streams of 900 parts (2,700 parts of one type, one sender, one target, in a burst)
    /// all complete, though the breaker trips a single bucket at 2,000 within a second.
    /// </summary>
    [Fact]
    public async Task Concurrent_streams_between_two_hubs_are_not_one_storm()
    {
        var client = ClientWithSilenceBound(TimeSpan.FromSeconds(10));

        var all = await Observable.Merge(Enumerable.Range(0, 3).Select(_ =>
                client.ObserveStreamed(new CountRequest(900), o => o.WithTarget(CreateHostAddress())).Count()))
            .ToList()
            .Should().Within(30.Seconds()).Emit();

        all.Should().Equal([901, 901, 901], "every stream delivers its 900 parts and its reply");
    }

    /// <summary>
    /// A stream built under one identity and subscribed under another leaves the SUBSCRIBER's identity in
    /// place once Subscribe returns — the builder's identity is applied in scopes only.
    /// </summary>
    [Fact]
    public async Task Subscribing_under_another_identity_leaves_the_subscribers_identity_in_place()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        var builder = new AccessContext { ObjectId = "user-a", Name = "user-a" };
        var subscriber = new AccessContext { ObjectId = "user-b", Name = "user-b" };
        var before = access.Context;
        try
        {
            access.SetContext(builder);
            var stream = client.ObserveStreamed(new CountRequest(3), o => o.WithTarget(CreateHostAddress()));
            access.SetContext(subscriber);

            var all = new System.Reactive.Subjects.ReplaySubject<IMessageDelivery>();
            using var subscription = stream.Subscribe(all);

            access.Context?.ObjectId.Should().Be("user-b", "the builder's identity must not leak onto the subscriber's flow");
            (await all.ToList().Should().Within(10.Seconds()).Emit()).Should().HaveCount(4);
        }
        finally
        {
            access.SetContext(before);
        }
    }

    /// <summary>
    /// A stream built WITHOUT an identity and subscribed under a privileged one posts without it — it never
    /// borrows the subscriber's identity (the request must fail closed, as an eager Observe post would).
    /// </summary>
    [Fact]
    public async Task A_stream_built_without_an_identity_never_posts_as_its_subscriber()
    {
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        var before = access.Context;
        try
        {
            access.SetContext(null);
            var stream = client.ObserveStreamed(new WhoAsks(), o => o.WithTarget(CreateHostAddress()));
            access.SetContext(new AccessContext { ObjectId = "privileged-b", Name = "privileged-b" });

            var reply = await stream.Should().Within(10.Seconds()).Emit();

            ((CountDone)reply.Message).Mode.Should().NotBe("privileged-b", "the subscriber's identity must not ride a stream built without one");
            access.Context?.ObjectId.Should().Be("privileged-b", "the subscriber's own identity is restored");
        }
        finally
        {
            access.SetContext(before);
        }
    }
}
