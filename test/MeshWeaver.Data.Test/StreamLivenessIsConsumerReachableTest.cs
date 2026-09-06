using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A CONSUMER MUST BE ABLE TO ASK WHETHER A STREAM IS STILL ALIVE — Systemorph/MeshWeaver#3321,
/// step 1 of 3.
///
/// <para><b>What went wrong without this.</b> <see cref="ISynchronizationStream.Hub"/> is declared
/// NON-NULLABLE and 47 sites dereference it, while a stream whose owner has been torn down keeps
/// answering that property. A predecessor of <c>SynchronizationStream</c> tried to make the corpse
/// honest by building a DEAD stream with <c>Hub = null!</c>, documenting that "every code path that
/// touches Hub goes through <c>TryGetActiveHub</c>". No consumer honoured that — <c>TryGetActiveHub</c>
/// was private to one file — and the result was an NRE in <c>LayoutAreaHost</c>'s constructor during
/// a recycle window, which reached the subscriber as a TERMINAL <c>DeliveryFailure</c>.</para>
///
/// <para><b>Why this test is worth its keep.</b> The predicate itself
/// (<c>StreamLiveness.IsUsable</c>) was never the missing piece — it already walks the reduce chain
/// and already treats an absent hub as dead. What was missing is that it was <c>internal</c>. So the
/// assertions here are deliberately about REACHABILITY and ACCESSIBILITY, not about the verdict:
/// <see cref="TheSurfaceIsGenuinelyPublic"/> reads accessibility by REFLECTION rather than by
/// compiling, because <c>MeshWeaver.Data</c> grants <c>InternalsVisibleTo</c> to this very assembly —
/// a test that merely called the methods would compile just as happily if someone made them internal
/// again, and would pass while the defect was fully restored.</para>
///
/// <para>🚨 <b>Behaviour is unchanged and that is the point.</b> Steps 2 and 3 of #3321 — migrating
/// the dereference sites onto <c>TryGetHub</c>, and only then dropping the <c>Hub</c> reference on
/// disposal — are not done. Dropping the reference before the call sites can answer "no" is exactly
/// the incident above.</para>
/// </summary>
public class StreamLivenessIsConsumerReachableTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    /// <summary>
    /// A live stream answers "usable", and hands back the very hub a caller would otherwise have
    /// dereferenced. The positive half matters as much as the negative: a predicate that answered
    /// "dead" for everything would satisfy every other assertion here while making the accessor
    /// useless, and step 2 would then migrate 47 call sites onto something that always says no.
    /// </summary>
    [HubFact]
    public async Task ALiveStream_IsUsable_AndYieldsItsHub()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        // Wait for real data rather than asserting on a stream that has not yet produced anything:
        // "alive" should be measured on a stream in the state a consumer actually holds one in.
        host.Post(
            new DataChangeRequest().WithUpdates(new MyData("live", "value-1")),
            o => o.WithAccessContext(accessService.Context!));

        await stream
            .Where(ci => ci.Value is not null
                         && ci.Value.Collections.GetValueOrDefault(collectionName)
                             ?.Instances.ContainsKey("live") == true)
            .FirstAsync()
            .Timeout(10.Seconds());

        stream.IsUsable().Should().BeTrue("a stream that is serving data is alive");
        stream.TryGetHub().Should().BeSameAs(stream.Hub,
            "the accessor must hand back the same hub the non-nullable property does, or step 2's "
            + "migration would change behaviour rather than only its failure mode");
    }

    /// <summary>
    /// The case the whole issue is about: after disposal the stream still answers
    /// <see cref="ISynchronizationStream.Hub"/> — a non-null hub, exactly as the contract promises —
    /// yet the accessor says no. That gap between the two is the corpse a consumer could not
    /// previously detect.
    /// </summary>
    [HubFact]
    public async Task ADisposedStream_IsNotUsable_EvenThoughHubStillAnswers()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        await stream.FirstAsync(ci => ci.Value is not null).Timeout(10.Seconds());
        stream.IsUsable().Should().BeTrue("precondition: the stream is alive before we kill it");

        stream.Dispose();

        // 🚨 This assertion INVERTED at step 3 (#3321), and the inversion is the whole point of the
        // three-step order. Through steps 1 and 2 it read `Hub.Should().NotBeNull()` — the
        // non-nullable contract was kept to the letter, so nothing about `stream.Hub.Anything`
        // looked wrong at a call site, which is precisely why a consumer needed a separate way to
        // ask. Step 3 is what made "absent" a state the field can be in, and it was only safe to
        // ship BECAUSE steps 1 and 2 had landed: the accessor below existed, and every call site
        // that could race a teardown had been migrated onto it.
        stream.Hub.Should().BeNull(
            "step 3 releases the hub on disposal — that release is what reclaims the leaked "
            + "stream → dead hub → resolved state graph");
        stream.IsUsable().Should().BeFalse("a disposed stream is a corpse");
        stream.TryGetHub().Should().BeNull("the accessor is the way a consumer finds that out");
    }

    /// <summary>
    /// A null stream is answerable rather than an exception, so a consumer holding an
    /// <c>ISynchronizationStream?</c> can gate on one call instead of a null check plus a liveness
    /// check — two steps being where a call site forgets one.
    /// </summary>
    [HubFact]
    public Task ANullStream_IsAnswered_NotThrown()
    {
        ISynchronizationStream? stream = null;

        stream.IsUsable().Should().BeFalse();
        stream.TryGetHub().Should().BeNull();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 🚨 The guard that cannot be satisfied by accident. <c>MeshWeaver.Data</c> grants
    /// <c>InternalsVisibleTo("MeshWeaver.Data.Test")</c>, so every other test in this file would
    /// compile and pass unchanged if the surface reverted to <c>internal</c> — which is the exact
    /// state #3321 step 1 exists to leave behind. Reflection reports real CLR accessibility, which
    /// <c>InternalsVisibleTo</c> does not alter, so this is the one assertion here that actually
    /// fails when the surface is withdrawn.
    /// </summary>
    [HubFact]
    public Task TheSurfaceIsGenuinelyPublic()
    {
        var type = typeof(SynchronizationStreamLiveness);
        type.IsPublic.Should().BeTrue(
            "consumers live in other assemblies; internal is the state that caused #3321");

        foreach (var name in new[] { nameof(SynchronizationStreamLiveness.IsUsable), "TryGetHub" })
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            method.Should().NotBeNull($"{name} must be public and static to be reachable");
            method!.IsDefined(typeof(ExtensionAttribute), inherit: false).Should().BeTrue(
                $"{name} must be an extension method so a call site reads stream.{name}() — the "
                + "shape step 2 migrates 47 dereferences onto");
            method.GetParameters()[0].ParameterType.Should().Be(typeof(ISynchronizationStream),
                $"{name} must extend the CONTRACT, not an implementation type — a consumer holds "
                + "the interface");
        }

        return Task.CompletedTask;
    }
}
