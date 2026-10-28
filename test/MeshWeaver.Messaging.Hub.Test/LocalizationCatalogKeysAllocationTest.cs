using System;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins that <see cref="LocalizationCatalog.Keys"/> and <see cref="LocalizationCatalog.KeysFor"/> are
/// HELD sets, not a copy of the catalog per read (MeshWeaver#5799).
///
/// <para><b>What went wrong.</b> <c>Keys</c> was an expression-bodied property that copied the whole
/// English catalog (~2,600 keys) into a new <see cref="ImmutableHashSet{T}"/> on every read, and module
/// layout areas call <c>LocalizationCatalog.Keys.Contains(key)</c> once per label they render — each
/// read allocating the catalog's worth of <c>SortedInt32KeyNode&lt;ImmutableHashSet&lt;string&gt;.HashBucket&gt;</c>
/// tree nodes, the type that led a 2.57 GiB heap step on a production portal.</para>
///
/// <para>The negative control measures the old derivation with the same instrument and shows it fails
/// the same bound — so the instrument can fail.</para>
/// </summary>
public class LocalizationCatalogKeysAllocationTest(ITestOutputHelper output)
{
    private const int Reads = 200;
    private const long BytesPerReadBound = 256;

    [Fact]
    public void KeysContains_AllocatesNothingPerRead()
    {
        var key = PickKey();
        var perRead = Settled(() =>
        {
            var hits = 0;
            for (var i = 0; i < Reads; i++)
                if (LocalizationCatalog.Keys.Contains(key) && LocalizationCatalog.KeysFor("de").Contains(key))
                    hits++;
            return hits;
        }) / Reads;

        output.WriteLine($"Held: {perRead:N0} bytes per Keys/KeysFor read over {LocalizationCatalog.Keys.Count:N0} keys.");
        perRead.Should().BeLessThan(BytesPerReadBound,
            "the key set is built once with the catalog; a read must not copy it");
        LocalizationCatalog.Keys.Should().BeSameAs(LocalizationCatalog.Keys);
    }

    [Fact]
    public void NegativeControl_ThePerReadCopy_FailsTheSameBound()
    {
        var key = PickKey();
        // A plain sequence of the keys, as the catalog dictionary's Keys view was — NOT the held set,
        // which ToImmutableHashSet would hand back unchanged and so measure nothing.
        var held = LocalizationCatalog.Keys;
        var catalogKeys = held.ToArray();
        // The shape this fix removed: copy the catalog's keys into a fresh set on every read.
        var perRead = Settled(() =>
        {
            var hits = 0;
            for (var i = 0; i < Reads / 20; i++)
                if (catalogKeys.ToImmutableHashSet(StringComparer.Ordinal).Contains(key))
                    hits++;
            return hits;
        }) / (Reads / 20);

        output.WriteLine($"Copied: {perRead:N0} bytes per read over {held.Count:N0} keys.");
        perRead.Should().BeGreaterThan(BytesPerReadBound * 100,
            "the instrument must be able to see the per-read copy it exists to catch");
    }

    private static string PickKey()
    {
        LocalizationCatalog.Keys.Should().NotBeEmpty("the English catalog is the key list of record");
        foreach (var key in LocalizationCatalog.Keys)
            return key;
        throw new InvalidOperationException("unreachable");
    }

    private static long Settled(Func<int> action)
    {
        var best = long.MaxValue;
        for (var sample = 0; sample < 10; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(action());
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }
}
