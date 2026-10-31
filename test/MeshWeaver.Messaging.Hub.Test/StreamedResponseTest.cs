using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
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
    private record CountRequest(int Parts, bool Reversed = false, bool GoSilent = false) : IRequest<CountDone>;

    private record CountPart(int Index);

    private record CountDone(string Mode);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        configuration
            .WithTypes(typeof(CountRequest), typeof(CountPart), typeof(CountDone))
            .WithHandler<CountRequest>((hub, request) =>
            {
                var ask = request.Message;
                if (!request.AcceptsResponseParts())
                {
                    // The caller awaits ONE reply: never send it a part it would read as the reply.
                    hub.Post(new CountDone("single"), o => o.ResponseFor(request));
                    return request.Processed();
                }
                var order = Enumerable.Range(0, ask.Parts);
                if (ask.Reversed)
                    order = order.Reverse();   // what an unordered transport may do
                foreach (var i in order)
                    hub.PostResponsePart(request, new CountPart(i), i);
                if (!ask.GoSilent)
                    hub.PostFinalResponse(request, new CountDone("streamed"), ask.Parts);
                return request.Processed();
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(CountRequest), typeof(CountPart), typeof(CountDone));

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
}
