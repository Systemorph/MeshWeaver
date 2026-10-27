using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A deletion's drain never reports "no admitted write pending" while one is (Systemorph/MeshWeaver#7346
/// review). Since a write admitted at its owner's COMMIT lets a write to the same path through an open
/// scope, a new registration can be added under the root while an older one is released — the guard's
/// write registers, then the commit admission it overlapped is released (a no-op merge, a flush's own
/// answer). <see cref="RecentlyDeletedRegistry.AdmittedWritesSettled"/> scanned the LIVE dictionary, and a
/// ConcurrentDictionary enumeration is not a snapshot: it could pass the new entry's bucket before the
/// entry was added and reach the old entry's bucket after it was removed, seeing neither — and the
/// deletion then ran while the newer write was still on its way to the store. The drain now tests an
/// atomic snapshot.
///
/// <para>The window lies inside one enumeration, so the interleaving is driven by two threads: one keeps
/// handing an admission over to a fresh registration under the same root (register the new one FIRST,
/// release the old one second — so at every instant at least one is pending), the other keeps asking
/// the drain whether anything is pending.</para>
/// </summary>
public class AnOverlappingAdmissionHandoffIsNeverMissedByTheDrainTest
{
    private const int Handoffs = 300_000;

    [Fact]
    public void TheDrainNeverSettlesWhileAHandedOverWriteIsPending()
    {
        var registry = new RecentlyDeletedRegistry();
        const string root = "acme/space";
        string[] paths = ["acme/space/node"];

        // Negative control on the instrument: with nothing pending the drain DOES settle, synchronously.
        var settledWhenEmpty = false;
        using (registry.AdmittedWritesSettled(root).Subscribe(_ => settledWhenEmpty = true)) { }
        settledWhenEmpty.Should().BeTrue("an empty registry settles at once");

        var handingOver = 1;
        var falseSettles = 0;
        var held = registry.AdmitWrite(paths);
        Parallel.Invoke(
            () =>
            {
                try
                {
                    for (var i = 0; i < Handoffs; i++)
                    {
                        var next = registry.AdmitWrite(paths);
                        held.Dispose();
                        held = next;
                    }
                }
                finally
                {
                    Volatile.Write(ref handingOver, 0);
                }
            },
            () =>
            {
                while (Volatile.Read(ref handingOver) != 0)
                {
                    var settled = false;
                    using (registry.AdmittedWritesSettled(root).Subscribe(_ => settled = true))
                        if (settled)
                            Interlocked.Increment(ref falseSettles);
                }
            });

        falseSettles.Should().Be(0,
            "at every instant one admitted write is pending, so the drain must never report none");
        held.Dispose();
    }
}
