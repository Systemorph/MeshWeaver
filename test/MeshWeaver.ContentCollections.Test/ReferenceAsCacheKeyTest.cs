using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A <see cref="WorkspaceReference"/> IS A CACHE KEY, so it must have VALUE equality.</b>
/// Systemorph/MeshWeaver#3432.
///
/// <para><b>The mechanism.</b> A positional <c>record</c> whose member is a collection does NOT get
/// value equality for free: the compiler-generated <c>Equals</c>/<c>GetHashCode</c> go through
/// <c>EqualityComparer&lt;IReadOnlyCollection&lt;string&gt;&gt;.Default</c>, which for a
/// <c>string[]</c> is REFERENCE equality. <c>Workspace._localStreamCache</c> is keyed on the
/// reference, so such a reference misses the cache on EVERY call — and every miss constructs a
/// <c>SynchronizationStream</c>, hence a hosted <c>sync/{id}</c> sub-hub with its own Autofac
/// scope, <c>TypeRegistry</c> and <c>JsonSerializerOptions</c> (~390 KB), registered for disposal
/// on the HUB-LIFETIME node hub. One permanent hub per read, released only when the node hub dies.
/// It is the #3952 read-path defect reached by a different route: there the cache was BYPASSED,
/// here it is UNHITTABLE.</para>
///
/// <para><b>Measured on production</b> (memex.systemorph.com, pod <c>…-ztqz8</c>, pid 1, uptime
/// 870 min, image <c>afde4eabe0</c>, 2026-09-16): 312 live <c>sync/</c> hubs, every one of them
/// attributed to the stream that minted it, of which 80 were duplicate mints of a value-identical
/// <c>(host, reference)</c> pair. TWENTY-FOUR of those were ONE pair —
/// <c>(Doc/Architecture, ContentCollectionReference["content"])</c> — and comparing the LIVE
/// reference objects on the heap said <c>refsEqual=NO</c> for that group while EVERY other
/// duplicate group, each a reference type with value equality, said <c>refsEqual=YES</c>. That one
/// group also went 18 → 24 across two readings 34 minutes apart on the same pod and pid, while the
/// whole population moved 311 → 312: it mints on every read, for ever.
/// See <c>Doc/Architecture/AReferenceThatCannotBeAKey</c>.</para>
///
/// <para>🚨 <b>Both directions are asserted.</b>
/// <see cref="AConfiguredReduce_StillMintsOneSyncHubPerCall"/> drives the deliberately-uncached
/// configured overload through the same counter and shows it grow. Without it, "the count did not
/// move" would also pass on a build where the counter is blind.</para>
/// </summary>
public class ReferenceAsCacheKeyTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Reads driven per arm. Small on purpose — the defect is monotone, so a handful
    /// separates "one hub per read" (grows with N) from "one hub per reference" (flat).</summary>
    private const int Reads = 5;

    private readonly string contentBasePath =
        Path.Combine(AppContext.BaseDirectory, "Files", "CacheKeyContent");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(contentBasePath);
        return base.ConfigureHost(configuration)
            .AddContentCollections()
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = contentBasePath,
                Settings = new Dictionary<string, string> { ["BasePath"] = contentBasePath }
            });
    }

    /// <summary>
    /// 🚨 <b>THE UNIT HALF.</b> Two references naming the same collection are the same key, or the
    /// cache below cannot work. This is the assertion that fails on the unfixed build.
    /// </summary>
    [Fact]
    public void TwoValueIdenticalReferences_AreEqualAndShareAHash()
    {
        var a = new ContentCollectionReference(["content"]);
        var b = new ContentCollectionReference(["content"]);

        a.Should().NotBeSameAs(b, "the two arms must be distinct instances or the test is vacuous");
        a.ToString().Should().Be(b.ToString(), "they name the same collection");

        a.Should().Be(b,
            "a WorkspaceReference is a cache key — a record whose member is a collection gets "
            + "REFERENCE equality from the compiler, which no ConcurrentDictionary can ever hit");
        a.GetHashCode().Should().Be(b.GetHashCode(),
            "equal keys must share a hash or the dictionary lookup never reaches Equals");

        // The empty/absent forms are keys too, and both must be stable.
        new ContentCollectionReference().Should().Be(new ContentCollectionReference());
        new ContentCollectionReference([]).Should().Be(new ContentCollectionReference([]));
        new ContentCollectionReference(["content"]).Should().NotBe(new ContentCollectionReference(["other"]),
            "different collection names are genuinely different keys — the fix must not collapse them");

        // 🚨 NULL and EMPTY are ONE reference. `ToString()` answers `collection` for both, and the
        // reducer branches on `collectionNames is null || collectionNames.Count == 0` and returns
        // GetAllCollectionConfigs() for both. An equality that separated them would leave the whole
        // defect alive for a caller that alternates the two spellings — two keys, two streams, two
        // permanent hubs, for one read.
        //
        // 🚨 The NULL arm must be spelled with an explicit cast. `new ContentCollectionReference()`
        // does NOT produce a null member: a params-collection parameter called with no arguments is
        // handed an EMPTY collection, so both arms would be empty and the assertion could not fail.
        // The null IS reachable in production — ContentCollectionsExtensions.CreateCollectionConfigStream
        // declares `string[]? collectionNames = null`, leaves it null when the path carries no names,
        // and passes it straight in.
        var nullNames = new ContentCollectionReference((IReadOnlyCollection<string>?)null);
        var empty = new ContentCollectionReference([]);
        nullNames.CollectionNames.Should().BeNull(
            "the precondition of this arm: one side really does carry a null member");
        empty.CollectionNames.Should().NotBeNull("and the other really does carry an empty one");
        nullNames.ToString().Should().Be(empty.ToString(),
            "the precondition: these two already render as the same reference");
        nullNames.Should().Be(empty,
            "null and empty both mean ALL collections, so they are one cache key");
        nullNames.GetHashCode().Should().Be(empty.GetHashCode(),
            "equal keys must share a hash or the dictionary never reaches Equals");
    }

    /// <summary>
    /// 🚨 <b>The other two references fixed alongside it, asserted by BEHAVIOUR.</b> The reflection
    /// guard below checks that a hand-written <c>Equals</c>/<c>GetHashCode</c> EXISTS; it cannot
    /// check that it is right. A wrong sequence comparison or a hash that disagrees with it would
    /// sail through the guard and still break the cache — silently, and in the direction that costs
    /// a permanent hub per call. So each one is exercised with independently allocated instances.
    /// </summary>
    [Fact]
    public void TheSiblingReferencesWithArrayMembers_CompareByValue()
    {
        // AggregateWorkspaceReference — a WorkspaceReference<EntityStore>[] member.
        var aggA = new AggregateWorkspaceReference(new CollectionsReference("A"), new CollectionsReference("B"));
        var aggB = new AggregateWorkspaceReference(new CollectionsReference("A"), new CollectionsReference("B"));
        aggA.Should().NotBeSameAs(aggB, "distinct instances, or the assertion is vacuous");
        aggA.Should().Be(aggB, "equal reference sequences are one cache key");
        aggA.GetHashCode().Should().Be(aggB.GetHashCode(), "equal keys must share a hash");
        aggA.Should().NotBe(new AggregateWorkspaceReference(new CollectionsReference("A")),
            "a DIFFERENT sequence must stay a different key — the fix must not collapse everything to equal");
        aggA.Should().NotBe(new AggregateWorkspaceReference(new CollectionsReference("B"), new CollectionsReference("A")),
            "the sequence is ordered, so a permutation is a different reference");
        new AggregateWorkspaceReference().Should().Be(new AggregateWorkspaceReference(),
            "the empty aggregate is a stable key too");

        // CombinedStreamReference — a StreamIdentity[] member.
        var one = new Address("test", "one");
        var two = new Address("test", "two");
        var comA = new CombinedStreamReference(new StreamIdentity(one, null), new StreamIdentity(two, "p"));
        var comB = new CombinedStreamReference(new StreamIdentity(one, null), new StreamIdentity(two, "p"));
        comA.Should().NotBeSameAs(comB, "distinct instances, or the assertion is vacuous");
        comA.Should().Be(comB, "equal identity sequences are one cache key");
        comA.GetHashCode().Should().Be(comB.GetHashCode(), "equal keys must share a hash");
        comA.Should().NotBe(new CombinedStreamReference(new StreamIdentity(one, null)),
            "a different identity sequence stays a different key");
        comA.Should().NotBe(new CombinedStreamReference(new StreamIdentity(one, "p"), new StreamIdentity(two, "p")),
            "the partition is part of the identity, so changing it changes the reference");
    }

    /// <summary>
    /// 🚨 <b>THE BEHAVIOURAL HALF.</b> Repeated plain reads of the same collection-config reference
    /// share ONE stream, so the owning hub's <c>sync/</c> population is bounded by DISTINCT
    /// REFERENCES, not by reads. On the unfixed build this reads <c>+5</c>.
    /// </summary>
    [HubFact]
    public async Task ReadingTheSameCollectionReference_DoesNotMintASyncHubPerRead()
    {
        var host = GetHost();
        await host.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();

        // The FIRST read legitimately builds the stream; the baseline is taken after it, so what is
        // measured is the SECOND read onwards — exactly the population this issue is about.
        var first = workspace.GetStream(new ContentCollectionReference(["content"]), null);
        (first is not null).Should().BeTrue("the collection-config reduction must be registered on this hub");

        // 🚨 SCOPED TO WHAT THESE CALLS MINTED, never to the host's total. A global before/after
        // delta is the shape that produced #4300's false +6: an unrelated stream created between
        // the two samples lands inside the measurement. The reads' OWN hub addresses cannot be
        // polluted by anything else on the host.
        var minted = new List<Address>();
        for (var i = 0; i < Reads; i++)
        {
            var again = workspace.GetStream(new ContentCollectionReference(["content"]), null);
            ReferenceEquals(again, first).Should().BeTrue(
                "a plain reduce of an equal reference is SHARED — a second instance is a second "
                + "SynchronizationStream and a second permanent sync/ hub on this node hub");
            minted.Add(again!.Hub.Address);
        }

        var distinct = minted.Distinct().Count();
        Output.WriteLine($"DIAG shared: reads={Reads} distinctSyncHubs={distinct}");
        distinct.Should().Be(1,
            "all five reads must resolve to the SAME sync/ hub — the population is bounded by "
            + "distinct references, not by reads. Measured on memex.systemorph.com 2026-09-16, ONE "
            + "such reference had minted 24 hubs on one node hub, up from 18 half an hour earlier");
        minted[0].Should().Be(first!.Hub.Address, "and it is the hub the first read built");
    }

    /// <summary>
    /// 🚨 <b>THE CONTROL IN THE OTHER DIRECTION — the counter can see growth.</b> A genuinely
    /// caller-specific configuration is uncached BY CONTRACT, so five calls make five hubs. If this
    /// ever goes flat the counter has stopped measuring and the arm above is vacuous.
    /// </summary>
    [HubFact]
    public async Task AConfiguredReduce_StillMintsOneSyncHubPerCall()
    {
        var host = GetHost();
        await host.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();

        var shared = workspace.GetStream(new ContentCollectionReference(["content"]), null);

        // 🚨 SCOPED, for the same reason as the arm above: assert the identity of the hubs THESE
        // calls minted, not the host's total. `Be(Reads)` on a global delta can fail when every
        // call behaved correctly, and relaxing it to `>=` would make a missing mint invisible —
        // both readings are wrong, which is what #4300 cost.
        var minted = new List<Address>();
        for (var i = 0; i < Reads; i++)
        {
            var configured = workspace.GetStream(
                new ContentCollectionReference(["content"]), x => x.WithClientId($"caller-{i}"));
            minted.Add(configured!.Hub.Address);
        }

        Output.WriteLine($"DIAG configured: calls={Reads} distinctSyncHubs={minted.Distinct().Count()}");
        minted.Distinct().Should().HaveCount(Reads,
            "a CONFIGURED reduce is caller-specific and therefore uncached BY CONTRACT — each call "
            + "builds its own SynchronizationStream and its own sync/ hub. This is what proves the "
            + "shared arm's single-hub reading is a real cache hit and not a blind assertion");
        minted.Should().NotContain(shared!.Hub.Address,
            "and none of them may be the SHARED stream's hub — otherwise the two arms are measuring "
            + "the same thing and neither discriminates");

        var live = host.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Select(h => h.Address).ToHashSet();
        minted.Should().OnlyContain(a => live.Contains(a),
            "every hub these calls minted must actually be hosted — a counted address that is not "
            + "in the collection would mean the count is not measuring the population");
    }

    /// <summary>
    /// 🚨 <b>THE GUARD, so the next one is caught before it ships.</b> Every
    /// <see cref="WorkspaceReference"/> whose record-equality would compare an
    /// identity-compared member — an array, any non-string <c>IEnumerable</c>, or a
    /// <c>Lazy&lt;&gt;</c> — must DECLARE its own <c>Equals</c> and <c>GetHashCode</c>.
    ///
    /// <para>Three types in <c>src/</c> already do, each written that way deliberately
    /// (<c>CollectionsReference</c>, <c>LayoutAreaReference</c> — *"exclude the parameters
    /// field"* — and <c>Address</c>); three were written without it and #3432 measured one of
    /// them costing 24 permanent hubs on a single node hub.</para>
    ///
    /// <para>The denominator is asserted so the guard cannot pass having scanned nothing: a
    /// reference type moving assembly, or the scan failing to load one, reds here instead of
    /// going quietly green.</para>
    /// </summary>
    [Fact]
    public void EveryWorkspaceReferenceWithAnIdentityComparedMember_DeclaresValueEquality()
    {
        // Force-load both reference homes by naming a type in each; a reflection scan over
        // "loaded assemblies" alone would silently miss an assembly nothing has touched yet.
        var assemblies = new[]
        {
            typeof(CollectionsReference).Assembly,          // MeshWeaver.Data.Contract
            typeof(ContentCollectionReference).Assembly,    // MeshWeaver.ContentCollections
        };

        var references = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(WorkspaceReference).IsAssignableFrom(t))
            .ToArray();

        references.Should().Contain(typeof(CollectionsReference),
            "the scan must reach the type that already carries the overrides — it is the positive "
            + "control that proves the guard is looking in the right place");
        references.Should().Contain(typeof(ContentCollectionReference),
            "and the type #3432 measured, or the guard has stopped covering its own subject");

        // 🚨 THE DETECTOR IS ITSELF TESTED, IN BOTH DIRECTIONS — the first version of this guard
        // asked whether the type DECLARES Equals(T), which a record ALWAYS does (the compiler
        // synthesises it), so it passed on the unfixed build having checked nothing. What separates
        // a hand-written override from the synthesised one is [CompilerGenerated]; these two
        // assertions are what prove that predicate can say both YES and NO.
        DeclaresValueEquality(typeof(ProbeReference)).Should().BeFalse(
            "a record with an array member and NO hand-written equality is exactly the shape this "
            + "guard exists to catch — if the detector says yes here it cannot fail at all");
        DeclaresValueEquality(typeof(CollectionsReference)).Should().BeTrue(
            "and it must say yes for the type that does carry hand-written Equals/GetHashCode");

        var needsOverride = references
            .Where(t => InstanceFields(t).Any(f => IsIdentityCompared(f.FieldType)))
            .ToArray();

        Output.WriteLine(
            $"DIAG scanned={references.Length} needOverride={needsOverride.Length}: "
            + string.Join(", ", needsOverride.Select(t => t.Name)));

        needsOverride.Should().NotBeEmpty(
            "at least CollectionsReference and ContentCollectionReference carry a collection member "
            + "— an empty set means the detector stopped detecting and the guard checks nothing");

        var missing = needsOverride
            .Where(t => !DeclaresValueEquality(t))
            .Select(t => t.FullName)
            .ToArray();

        missing.Should().BeEmpty(
            "a WorkspaceReference is a CACHE KEY (Workspace._localStreamCache, "
            + "SynchronizationStream.sharedReduceCache). A record member that is an array, a "
            + "non-string IEnumerable or a Lazy<> is compared BY REFERENCE by the compiler-generated "
            + "equality, so the key can never be hit and every read mints a permanent sync/ hub on "
            + "the owning node hub. Declare Equals(T) and GetHashCode() like CollectionsReference does");
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.DeclaringType == t);

    /// <summary>A field type the compiler-generated record equality compares by REFERENCE.</summary>
    private static bool IsIdentityCompared(Type t) =>
        t != typeof(string)
        && (t.IsArray
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Lazy<>))
            || typeof(IEnumerable).IsAssignableFrom(t));

    /// <summary>
    /// True only when BOTH members are HAND-WRITTEN on <paramref name="t"/>. A record always
    /// <em>declares</em> <c>Equals(T)</c> and <c>GetHashCode()</c> — the compiler synthesises them —
    /// so "does it declare one" is a question that cannot answer no, and the first version of this
    /// guard passed on the unfixed build for exactly that reason. The synthesised members carry
    /// <see cref="CompilerGeneratedAttribute"/>; a hand-written override does not.
    /// </summary>
    private static bool DeclaresValueEquality(Type t) =>
        IsHandWritten(t.GetMethod("Equals", BindingFlags.Instance | BindingFlags.Public, [t]), t)
        && IsHandWritten(t.GetMethod(nameof(GetHashCode), BindingFlags.Instance | BindingFlags.Public, []), t);

    private static bool IsHandWritten(MethodInfo? m, Type t) =>
        m is not null
        && m.DeclaringType == t
        && !m.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    /// <summary>The detector's negative sample: an array member, no hand-written equality. It is
    /// never scanned — it exists so the predicate above is asserted to be able to say NO.</summary>
    private sealed record ProbeReference(params string[] Names) : WorkspaceReference<object>;

    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);
}
