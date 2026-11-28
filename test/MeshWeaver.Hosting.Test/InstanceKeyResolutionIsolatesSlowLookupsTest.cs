using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A slow lookup for one key must not starve the others: each request has its own bounded budget,
/// a verified key is a brief fallback for a store that does not answer, a revoke drops it at once, and a 503
/// is answered only when the store reached no verdict. Virtual time throughout.
/// </summary>
public class InstanceKeyResolutionIsolatesSlowLookupsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private sealed class Store(TestScheduler scheduler)
    {
        public Dictionary<string, TimeSpan> Delay { get; } = new();
        public Dictionary<string, string> Keys { get; } = new();
        public int Reads;
        public bool Down;

        public (string Header, string Hash) AddInstance(string id, TimeSpan delay)
        {
            var raw = InstanceKeys.Generate();
            var hash = InstanceKeys.Hash(raw);
            Delay[hash] = delay;
            Keys[hash] = id;
            return (InstanceKeys.AuthorizationHeader(raw), hash);
        }

        public IObservable<NodeReadOutcome> Read(string path)
        {
            Reads++;
            if (Down)
                return Observable.Return(NodeReadOutcome.Unavailable(new TimeoutException("store unreachable")));
            foreach (var (hash, id) in Keys)
            {
                var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";
                var instancePath = $"acme/_Instance/{id}";
                if (path == indexPath)
                    return Observable.Return(NodeReadOutcome.Present(MeshNode.FromPath(indexPath) with
                    {
                        Content = new MeshWeaverInstanceIndex { KeyHash = hash, InstancePath = instancePath, InstanceId = id },
                    })).Delay(Delay[hash], scheduler);
                if (path == instancePath)
                    return Observable.Return(NodeReadOutcome.Present(MeshNode.FromPath(instancePath) with
                    {
                        Content = new MeshWeaverInstance { InstanceId = id, KeyHash = hash },
                    })).Delay(Delay[hash], scheduler);
            }
            return Observable.Return(NodeReadOutcome.Absent);
        }
    }

    private (InstanceRegistryAuthenticator Auth, Store Store, TestScheduler Scheduler) Arrange()
    {
        var scheduler = new TestScheduler();
        var store = new Store(scheduler);
        var auth = new InstanceRegistryAuthenticator(GetHost(), NullLogger<InstanceRegistryAuthenticator>.Instance)
        {
            ReadOverride = store.Read,
            ReadBudget = Budget,
            DeadlineScheduler = scheduler,
            VerifiedKeyTtl = TimeSpan.FromMinutes(2),
        };
        return (auth, store, scheduler);
    }

    private static List<InstanceAuthResult> Run(InstanceRegistryAuthenticator auth, string header)
    {
        var results = new List<InstanceAuthResult>();
        auth.AuthenticateOutcome(header).Subscribe(results.Add);
        return results;
    }

    [Fact]
    public void OneSlowLookup_DoesNotStarveOtherKeys_AndEachRequestHasItsOwnBudget()
    {
        var (auth, store, scheduler) = Arrange();
        var (slow, _) = store.AddInstance("slow", TimeSpan.FromSeconds(60));
        var (fast1, _) = store.AddInstance("fast1", TimeSpan.FromMilliseconds(100));
        var (fast2, _) = store.AddInstance("fast2", TimeSpan.FromMilliseconds(100));

        var slowResults = Run(auth, slow);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(9).Ticks);
        // The slow one has spent 9 of ITS 10 seconds; a request arriving now still gets a full budget.
        var fastResults = new[] { Run(auth, fast1), Run(auth, fast2) };
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks + 1);

        slowResults.Should().ContainSingle();
        slowResults[0].IsUnavailable.Should().BeTrue("the slow lookup alone is answered retryable at its own deadline");

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        foreach (var r in fastResults)
        {
            r.Should().ContainSingle();
            r[0].IsUnavailable.Should().BeFalse("a genuine key must never be told 503 because another lookup is slow");
            r[0].Instance.Should().NotBeNull();
        }
    }

    [Fact]
    public void AVerifiedKey_IsStillAnswered_WhenALaterLookupIsSlowOrTheStoreIsDown()
    {
        var (auth, store, scheduler) = Arrange();
        var (header, hash) = store.AddInstance("prod", TimeSpan.FromMilliseconds(10));

        var first = Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        first[0].Instance.Should().NotBeNull();

        // The lookup now takes longer than the budget.
        store.Delay[hash] = TimeSpan.FromSeconds(60);
        var slow = Run(auth, header);
        scheduler.AdvanceBy(Budget.Ticks + 1);
        slow.Should().ContainSingle();
        slow[0].IsUnavailable.Should().BeFalse("a key verified inside the window is not 503 because one lookup is slow");
        slow[0].Instance.Should().NotBeNull();

        // And the store being down is no different.
        store.Down = true;
        var down = Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        down[0].Instance.Should().NotBeNull("a verified key survives a store outage inside the window");
    }

    [Fact]
    public void ARevoke_InvalidatesTheRememberedKey_AndTheWindowExpires()
    {
        var (auth, store, scheduler) = Arrange();
        var (header, hash) = store.AddInstance("prod", TimeSpan.FromMilliseconds(10));
        Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        // Revoked while the store is unreachable: without the invalidation the key would still pass.
        store.Down = true;
        auth.Invalidate(hash);
        var afterRevoke = Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        afterRevoke.Should().ContainSingle();
        afterRevoke[0].Instance.Should().BeNull("a revoked key is never served from memory");
        afterRevoke[0].IsUnavailable.Should().BeTrue();

        // The store answering is authoritative: an absent key is refused even inside the window.
        store.Down = false;
        var (header2, hash2) = store.AddInstance("other", TimeSpan.FromMilliseconds(10));
        Run(auth, header2);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        store.Keys.Remove(hash2);
        var gone = Run(auth, header2);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        gone[0].Instance.Should().BeNull();
        gone[0].IsUnavailable.Should().BeFalse();

        // Expiry: past the window an outage is a 503 again.
        var (header3, _) = store.AddInstance("third", TimeSpan.FromMilliseconds(10));
        Run(auth, header3);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        scheduler.AdvanceBy(TimeSpan.FromMinutes(3).Ticks);
        store.Down = true;
        var expired = Run(auth, header3);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        expired[0].IsUnavailable.Should().BeTrue("the remembered answer is only a brief fallback");
    }

    [Fact]
    public void AnUnreachableStore_AnswersRetryable_AndCachesNothing()
    {
        var (auth, store, scheduler) = Arrange();
        var (header, _) = store.AddInstance("prod", TimeSpan.FromMilliseconds(10));
        store.Down = true;

        var down = Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        down[0].IsUnavailable.Should().BeTrue();

        store.Down = false;
        var up = Run(auth, header);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        up[0].Instance.Should().NotBeNull("the outage was not remembered");
    }
}
