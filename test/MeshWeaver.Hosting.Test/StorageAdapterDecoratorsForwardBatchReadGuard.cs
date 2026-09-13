using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A DECORATOR THAT DOES NOT FORWARD A DEFAULT INTERFACE MEMBER SILENTLY DISABLES IT FOR
/// EVERYTHING BELOW</b> — and nothing about the results says so.
///
/// <para><b>The shape.</b> <see cref="IStorageAdapter.ReadMany"/> has a DEFAULT implementation
/// (<c>Observable.Merge(paths.Select(Read))</c>). A decorator that declares no <c>ReadMany</c>
/// therefore does not "pass the call through" — dispatch lands on the default INSIDE that
/// decorator, which fans the batch back out into one point read per path through the decorator's
/// own <c>Read</c>. Everything below it, however correctly it batches, is unreachable. The emitted
/// nodes are identical either way; only the round-trips and the FAILURE MODES differ, so the
/// omission is invisible to every functional test of the result.</para>
///
/// <para><b>It has happened twice in this file's neighbourhood.</b>
/// <c>VersionWritingStorageAdapter</c> carries a 🚨 comment on <c>Changes</c> recording the first
/// time — a missing forward stopped every synced query from re-emitting and reddened ~25 tests. The
/// same type then did it again with <c>ReadMany</c> (#4200): it is the production chain's third
/// layer (<c>SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → PersistenceService</c>),
/// so its single omission degraded every batched read in every deployed host — which is how a
/// half-provisioned partition came to be spelled exactly like an absent node in the boot
/// completeness sweep. A review caught the second one; this guard is so a third does not need to be
/// caught by a reviewer.</para>
///
/// <para>🚨 <b>The denominator, stated.</b> This guard checks ONE member, <c>ReadMany</c>, over the
/// DECORATOR shape — a concrete <see cref="IStorageAdapter"/> built over another one. It does NOT
/// check the other default members that carry the identical hazard: measured while writing it,
/// <c>WriteMany</c> is un-forwarded by three of these five decorators and <c>DeleteMany</c> by two.
/// Whether each of those is deliberate is a separate question from this one and is not answered
/// here. It also does not cover an adapter that REPLACES the chain rather than decorating it
/// (<c>RoutingProxyAdapter</c> takes a hub and a router, not an inner adapter) — that one carries
/// its own override, but no structural rule holds it there.</para>
/// </summary>
public class StorageAdapterDecoratorsForwardBatchReadGuard
{
    /// <summary>The member whose default silently degrades a batch when a decorator omits it.</summary>
    private const string Member = nameof(IStorageAdapter.ReadMany);

    /// <summary>
    /// The assemblies that define storage adapters: the contract (for any adapter shipped beside
    /// the interface) and the hosting assembly where the production chain lives.
    /// </summary>
    private static IEnumerable<Assembly> ScannedAssemblies =>
        [typeof(IStorageAdapter).Assembly, typeof(PersistenceService).Assembly];

    /// <summary>
    /// A DECORATOR: a concrete <see cref="IStorageAdapter"/> that is CONSTRUCTED OVER another one.
    /// That constructor parameter is exactly what makes it able to intercept — and therefore to
    /// degrade — a default member on its way down.
    /// </summary>
    private static bool IsDecorator(Type type) =>
        type is { IsAbstract: false, IsInterface: false }
        && typeof(IStorageAdapter).IsAssignableFrom(type)
        && type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => typeof(IStorageAdapter).IsAssignableFrom(p.ParameterType)));

    /// <summary>
    /// Does <paramref name="type"/> declare the member ITSELF? Inheriting it is not forwarding:
    /// the default is precisely what must not be reached. Explicit interface implementations are
    /// non-public and name-mangled, so both spellings count.
    /// </summary>
    private static bool Declares(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == Member || m.Name.EndsWith("." + Member, StringComparison.Ordinal));

    private static List<Type> Decorators() =>
        ScannedAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(IsDecorator)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryStorageAdapterDecorator_ForwardsTheBatchedRead()
    {
        var silent = Decorators()
            .Where(t => !Declares(t))
            .Select(t => $"{t.FullName} declares no {Member}")
            .ToList();

        silent.Should().BeEmpty(
            $"a decorator that omits {Member} does not pass the call through — dispatch lands on "
            + "the interface DEFAULT inside it, the batch is fanned back into one point read per "
            + "path through that decorator's own Read, and every batched implementation below it "
            + "becomes unreachable. Nothing in the emitted nodes says so. Forward it (pure "
            + "delegation is usually the whole implementation; a scoping or remapping decorator "
            + "applies its rule to the SET first). Offenders:\n  "
            + string.Join("\n  ", silent));
    }

    /// <summary>
    /// 🚨 The scanner must actually FIND the decorators — a predicate that matched nothing would
    /// satisfy the test above on an empty set, which is the same defect one level up. Bound to a
    /// COUNT and to the SHAPE, never to specific type names: a guard that names symbols inherits
    /// every rename as a false failure.
    /// </summary>
    [Fact]
    public void TheScannerSeesTheDecoratorsItClaimsTo()
    {
        var decorators = Decorators();

        decorators.Count.Should().BeGreaterThanOrEqualTo(5,
            $"the production chain alone contributes three (SubtreeDeletionGuard, "
            + $"MonotonicWriteGuard, VersionWriting) and the provider-side wrappers two more; "
            + $"seeing fewer means the predicate stopped matching and the guard above is vacuous. "
            + $"Saw: {string.Join(", ", decorators.Select(t => t.Name))}");

        decorators.Should().OnlyContain(t => typeof(IStorageAdapter).IsAssignableFrom(t),
            "the set must be storage adapters, so the scanner is provably matching the right shape");
    }

    /// <summary>
    /// 🚨 Proven by MUTATION, on types planted right here: the SAME predicate must flag a decorator
    /// that omits the member and clear one that forwards it. Without this arm the guard could pass
    /// because <see cref="Declares"/> answers <c>true</c> for everything — a check that cannot fail
    /// is not a check.
    /// </summary>
    [Fact]
    public void TheRuleFlagsAForgetfulDecorator_AndClearsAForwardingOne()
    {
        IsDecorator(typeof(ForwardingDouble)).Should().BeTrue(
            "it is a concrete IStorageAdapter constructed over another one");
        IsDecorator(typeof(ForgetfulDouble)).Should().BeTrue(
            "it is the same shape — the difference the guard is about is the MEMBER, not the shape");

        Declares(typeof(ForwardingDouble)).Should().BeTrue(
            "it declares ReadMany, so the batch reaches the inner adapter");
        Declares(typeof(ForgetfulDouble)).Should().BeFalse(
            "it inherits the default instead of forwarding — exactly the omission the guard exists "
            + "to catch, and the arm that proves the guard CAN fail");
    }

    /// <summary>A decorator that forwards the batch — the shape the rule wants.</summary>
    private sealed class ForwardingDouble(IStorageAdapter inner) : InertDecorator(inner)
    {
        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => Inner.ReadMany(paths, options);
    }

    /// <summary>A decorator that does not — the mutation the rule must catch.</summary>
    private sealed class ForgetfulDouble(IStorageAdapter inner) : InertDecorator(inner);

    /// <summary>Everything a decorator must implement, doing nothing; the arms differ only in
    /// whether they declare <c>ReadMany</c>.</summary>
    private abstract class InertDecorator(IStorageAdapter inner) : IStorageAdapter
    {
        /// <summary>The wrapped adapter, exposed so a derived arm can forward to it without
        /// capturing the primary-constructor parameter a second time (CS9107).</summary>
        protected IStorageAdapter Inner => inner;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(
            string? parentPath)
            => inner.ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
