using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the sort key the language service emits with each completion.
///
/// <para>🚨 An editor does a plain STRING compare on <c>sortText</c> — Monaco orders its suggest
/// widget by (fuzzy score, sortText, label). Two consequences, and this suite exists for both:</para>
/// <list type="number">
///   <item>The key must reproduce the server's RANK, not the alphabet. Emitting Roslyn's own
///   <c>SortText</c> (an A→Z key) discarded every ranking the service computed — usage, locality,
///   target-typing priority — the moment the list opened with nothing typed, which is exactly when
///   ranking is all there is. The reported symptom was "ordering in autocomplete is alphabetical".</item>
///   <item>The key must be ZERO-PADDED, or the string compare inverts the order at ten items
///   (<c>"10" &lt; "2"</c>) — the classic off-by-a-decade that only shows up on longer lists.</item>
/// </list>
/// </summary>
public class CompletionRankKeyTest
{
    [Fact]
    public void RankKey_SortsNumerically_AsAString()
    {
        // The trap: without padding, "10" sorts BEFORE "2" and the eleventh suggestion jumps
        // above the third.
        string.CompareOrdinal(MeshNodeLanguageService.RankKey(2), MeshNodeLanguageService.RankKey(10))
            .Should().BeNegative("rank 2 must sort above rank 10 under an ordinal compare");
        string.CompareOrdinal(MeshNodeLanguageService.RankKey(0), MeshNodeLanguageService.RankKey(1))
            .Should().BeNegative("the first suggestion sorts first");
        string.CompareOrdinal(MeshNodeLanguageService.RankKey(99), MeshNodeLanguageService.RankKey(100))
            .Should().BeNegative("and across every decade boundary");
    }

    [Fact]
    public void RankKey_PreservesAnEntireRankedList()
    {
        var keys = Enumerable.Range(0, 200).Select(MeshNodeLanguageService.RankKey).ToArray();

        keys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(keys,
            "a ranked list must survive the editor's string sort in the order the server ranked it");
        keys.Should().OnlyHaveUniqueItems("two suggestions sharing a key would order arbitrarily");
    }

    [Fact]
    public void RankKey_IsCultureInvariant()
    {
        // A culture with non-ASCII digits would otherwise emit a key that no ordinal compare orders
        // — the kind of defect that only appears on someone else's machine.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("ar-SA");
            MeshNodeLanguageService.RankKey(7).Should().Be("0007");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
