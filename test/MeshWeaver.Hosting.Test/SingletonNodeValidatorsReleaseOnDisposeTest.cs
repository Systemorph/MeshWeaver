using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#6584 / review of #7055: a consumer of <see cref="SingletonNodeValidators.Validators"/> keeps
/// that sequence for its own life, and may outlive the mesh's root. Once the holder is disposed, nothing the
/// sequence hands out may stay reachable through it — every validator holds the mesh hub.
///
/// <para>The trap this pins: the sequence used to be a C# iterator method. An iterator object enumerated on
/// the thread that CREATED it hands ITSELF out as the enumerator, and its generated fields — the last
/// <c>Current</c> and the hoisted copy of the array being walked — survive the end of the enumeration. So
/// when the first enumeration ran on the constructing thread, the consumer's reference to the sequence kept
/// the last validator (and the whole array) alive after the holder had cleared its own field. Whether it did
/// depended on which thread enumerated first, which is why <c>CopyMoveDeadlineReleasesHubTest</c> failed in
/// CI and passed locally. This test makes the construction, the enumeration and the dispose share ONE
/// thread, so the shape is deterministic.</para>
///
/// <para>The second route it pins: the sequence must not reach the HOLDER. The holder keeps its disposed
/// scope, and a disposed scope still references every service it resolved — measured with a fresh-enumerator
/// sequence that read the holder's field: both validators stayed alive. The sequence holds the array alone.</para>
/// </summary>
public sealed class SingletonNodeValidatorsReleaseOnDisposeTest
{
    [Fact]
    public void ADisposedHolder_ReleasesEveryValidator_EvenWhenEnumeratedOnTheConstructingThread()
    {
        var (validators, built) = ConstructEnumerateAndDispose();

        for (var i = 0; i < 3 && built.Any(w => w.IsAlive); i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }

        built.Should().HaveCount(2, "both registered validators were built by the enumeration");
        built.Where(w => w.IsAlive).Should().BeEmpty(
            "the consumer still holds the holder's validator sequence, but the holder is disposed — the "
            + "sequence must not keep a validator (and the mesh hub it holds) reachable");
        validators.Should().BeEmpty("a disposed holder's sequence is empty");
        GC.KeepAlive(validators);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IEnumerable<Lazy<INodeValidator>> Validators, WeakReference[] Built) ConstructEnumerateAndDispose()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new Lazy<INodeValidator>(() => new ProbeValidator()));
        services.AddScoped(_ => new Lazy<INodeValidator>(() => new ProbeValidator()));
        using var root = services.BuildServiceProvider();

        // Constructed, enumerated and disposed on THIS thread — the thread that created the sequence.
        var holder = new SingletonNodeValidators(root.GetRequiredService<IServiceScopeFactory>());
        var validators = holder.Validators;
        var built = Materialize(validators);
        holder.Dispose();
        return (validators, built);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Materialize(IEnumerable<Lazy<INodeValidator>> validators)
        => validators.Select(validator => new WeakReference(validator.Value)).ToArray();

    private sealed class ProbeValidator : INodeValidator
    {
        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => throw new NotSupportedException("the probe is only built, never asked");

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Read];
    }
}
