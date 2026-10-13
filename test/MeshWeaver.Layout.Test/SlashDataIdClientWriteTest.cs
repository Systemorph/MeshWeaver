using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Systemorph/MeshWeaver#6629: a client's data-bound write into a layout data id that contains
/// '/' (or '~', the other character RFC 6901 escapes) must reach the OWNER's
/// <c>host.GetDataStream&lt;T&gt;(id)</c> exactly as one into a plain id does. The id travels inside
/// a JSON pointer as an RFC 6901-escaped segment (<c>/data/"a~1b"/prop</c>), and every hop that turns
/// that pointer back into (collection, id) has to unescape it once — no more, no less.
/// <para>Control: the ids without an escaped character (<c>a|b</c>, <c>plain</c>) take the identical
/// path and were green before the fix, so a red '/' row names the escaping, not the harness.</para>
/// </summary>
public class SlashDataIdClientWriteTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "SlashDataId";

    /// <summary>The record the owner seeds and the client patches one property of.</summary>
    public record Probe
    {
        /// <summary>The property the client writes.</summary>
        public string? Prop { get; init; }
    }

    private readonly ReplaySubject<LayoutAreaHost> hosts = new(1);
    private string dataId = "plain";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Probe))
            .AddLayout(layout => layout.WithView(Area, (host, _) =>
            {
                host.UpdateData(dataId, new Probe { Prop = "seed" });
                hosts.OnNext(host);
                return Controls.Markdown("probe");
            }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(typeof(Probe)).AddLayoutClient();

    [Theory]
    [InlineData("a/b")]
    [InlineData("clickCancel_Busy/Cancel")]
    [InlineData("a~b")]
    [InlineData("a~1b")]
    [InlineData("a|b")]
    [InlineData("plain")]
    public async Task AClientPatchReachesTheOwner(string id)
    {
        dataId = id;
        var ct = TestContext.Current.CancellationToken;
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        var pointer = LayoutAreaReference.GetDataPointer(id);

        // Both owner frames (the seed and the rendered control) are on the client before it writes,
        // so no later owner frame can overtake the write.
        await stream.GetControlStream(Area)
            .Should().Within(10.Seconds()).Match(c => c is not null, "the area rendered on the client", ct);
        await stream.GetDataStream<Probe>(new JsonPointerReference(pointer))
            .Should().Within(10.Seconds()).Match(p => p is { Prop: "seed" }, "the client sees the owner's seed", ct);
        var host = await hosts.Should().Within(10.Seconds()).Emit("the area rendered", ct);

        stream.UpdatePointer("edited", pointer, new JsonPointerReference("prop"));

        await host!.Stream.GetDataStream<Probe>(id)
            .Should().Within(10.Seconds()).Match(p => p is { Prop: "edited" },
                $"the owner must receive a client write into data id '{id}'", ct);
    }
}
