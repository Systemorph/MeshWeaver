using System;
using System.Threading;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A <see cref="GetDataRequest"/> is ONE read and gets ONE answer</b> — the root of
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/5636">#5636</see>:
///
/// <code>
/// MESSAGE STORM detected in hub Hosting/Build: the key (sender=Hosting/Build,
///   target=portal/reads-rqEH…, type=GetDataResponse) exceeded 2000 messages within 1s
/// </code>
///
/// <para>The generic read handler (<c>DataExtensions.HandleGetDataRequest</c>) subscribed the owner's
/// workspace stream for the reference and posted a <see cref="GetDataResponse"/> for EVERY emission —
/// "no Take(1): updates flow continuously to the consumer". There is no such consumer: a requester
/// correlates the reply through an <c>AsyncSubject</c> that takes exactly one and is then removed, so
/// every later response arrives with no subject and is dropped ("No subject found for response …
/// treating as processed"). Meanwhile the owner's subscription lived until the OWNER died. So every
/// one-shot read ever served by a long-lived hub stayed subscribed, and each change to that node
/// shipped one dead response per read ever taken — the key #5636 reports (one long-lived node hub,
/// one reads hub, GetDataResponse), which on memex reached 2001 in a second
/// before the storm breaker cut it.</para>
///
/// <para>This measures the dead responses at the REQUESTER, which is where they were being dropped,
/// and requires none. The control proves the instrument can count one.</para>
/// </summary>
public class OneReadOneAnswerTest : MonolithMeshTestBase
{
    private const string NodeId = "OneReadOneAnswerProbe";
    private static readonly string NodePath = $"{TestPartition}/{NodeId}";

    /// <summary>How many times the node changes after the read was answered.</summary>
    private const int Changes = 5;

    private readonly DroppedResponseCapture _capture = new();

    public OneReadOneAnswerTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders(). The drop is reported at Debug by the
        // requester's MessageHub, so this provider — and only this provider — reads that category
        // at Debug; nothing else in the mesh changes level.
        => Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(_capture);
            l.AddFilter<DroppedResponseCapture>(typeof(MessageHub).FullName, LogLevel.Debug);
        });

    /// <summary>
    /// 🚨 THE MEASUREMENT. Read a node once, change it <see cref="Changes"/> times, and the reader
    /// must receive no response beyond the one it asked for.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AReadOfANodeThatThenChanges_IsAnsweredOnce_NotOncePerChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(new MeshNode(NodeId, TestPartition) { Name = "v0", NodeType = "Markdown" })
            .Should().Emit("the node must exist, or the read is answered as absent and never subscribes", cancellationToken: ct);

        var reader = GetClient();
        var first = await reader.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(NodePath)))
            .Take(1).Timeout(TimeSpan.FromSeconds(20)).Await(ct);
        first.Message.Should().BeOfType<GetDataResponse>();
        // The drop line names no hub, so the reading is a DELTA across the window below — taken
        // after the answered read, so the read itself cannot contribute.
        var before = _capture.Dropped;

        for (var i = 1; i <= Changes; i++)
        {
            var name = $"v{i}";
            await Mesh.GetMeshNodeStream(NodePath).Update(n => n with { Name = name })
                .Should().Emit($"change {i} must land, or there is nothing a live read could re-ship", cancellationToken: ct);
        }
        await Mesh.GetMeshNodeStream(NodePath).Where(n => n?.Name == $"v{Changes}")
            .Should().Emit("the owner must have seen every change before the barrier", cancellationToken: ct);

        // BARRIER: a second read, same reader, same owner. The owner answers it on its own turn, after
        // every emission it processed before — so any dead response for the FIRST read was posted
        // ahead of this one on the same route, and has reached the reader by the time this returns.
        await reader.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(NodePath)))
            .Take(1).Timeout(TimeSpan.FromSeconds(20)).Await(ct);

        (_capture.Dropped - before).Should().Be(0,
            "#5636: the read handler kept the owner's stream subscribed after answering, so each of the "
            + $"{Changes} changes shipped another GetDataResponse the reader had no subject for. One read, "
            + "one answer — the owner must stop listening once it has answered");
    }

    /// <summary>
    /// 🚨 THE CONTROL. A response the reader has no subject for — a raw post with no Observe — must be
    /// COUNTED, or the zero above is an instrument that cannot see a drop.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheCapture_CountsAResponseTheReaderHasNoSubjectFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(new MeshNode(NodeId, TestPartition) { Name = "v0", NodeType = "Markdown" })
            .Should().Emit(cancellationToken: ct);

        var reader = GetClient();
        var before = _capture.Dropped;
        reader.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(NodePath)));
        await reader.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(NodePath)))
            .Take(1).Timeout(TimeSpan.FromSeconds(20)).Await(ct);

        (_capture.Dropped - before).Should().Be(1,
            "the un-observed read's single answer must be counted as a drop");
    }

    /// <summary>Counts the requester-side "no subject" drops of <see cref="GetDataResponse"/>, mesh-wide.</summary>
    private sealed class DroppedResponseCapture : ILoggerProvider
    {
        private int _dropped;

        internal int Dropped => Volatile.Read(ref _dropped);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(DroppedResponseCapture owner) : ILogger
        {
            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();
                public void Dispose() { }
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (formatter(state, exception).StartsWith(
                        "No subject found for response message GetDataResponse", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._dropped);
            }
        }
    }
}
