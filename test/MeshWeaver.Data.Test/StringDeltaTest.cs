using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Dedicated tests for <see cref="StringDelta"/> — the minimal single-splice text
/// delta used to (a) shrink a big-string patch to only its changed span and
/// (b) merge concurrent edits to disjoint parts of a document. Position-based
/// merge; overlapping edits are a version conflict, not a merge.
/// </summary>
public class StringDeltaTest
{
    [Fact]
    public void Compute_NoChange_IsEmpty()
    {
        var d = StringDelta.Compute("hello world", "hello world");
        d.IsEmpty.Should().BeTrue();
        d.Apply("hello world").Should().Be("hello world");
    }

    /// <summary>
    /// An insert in front of an emoji that shares its HIGH surrogate with the inserted emoji
    /// (🚨 U+1F6A8 and 💰 U+1F4B0 are both <c>\uD83D…</c>) must not split either pair: the
    /// splice carries whole characters only, so it survives a JSON round trip unchanged.
    /// </summary>
    [Fact]
    public void Compute_NeverSplitsASurrogatePair()
    {
        const string oldValue = "(\"\U0001F4B0 Earned\")";
        const string newValue = "(\"\U0001F6A8 Mine\")\n(\"\U0001F4B0 Earned\")";

        var d = StringDelta.Compute(oldValue, newValue);

        IsWholeText(d.Inserted).Should().BeTrue();
        IsWholeText(oldValue.Substring(0, d.Start)).Should().BeTrue();
        d.Apply(oldValue).Should().Be(newValue);

        // The wire round trip is where a lone surrogate turns into U+FFFD.
        var wire = System.Text.Json.JsonSerializer.Serialize(d.Inserted);
        var back = System.Text.Json.JsonSerializer.Deserialize<string>(wire)!;
        new StringDelta(d.Start, d.RemovedLength, back).Apply(oldValue).Should().Be(newValue);
    }

    /// <summary>A replace of one emoji by another that shares its high surrogate keeps both
    /// pairs whole on the prefix AND the suffix side.</summary>
    [Fact]
    public void Compute_EmojiReplacedBySibling_KeepsPairsWhole()
    {
        const string oldValue = "a \U0001F501 b";   // 🔁 \uD83D\uDD01
        const string newValue = "a \U0001F510 b";   // 🔐 \uD83D\uDD10

        var d = StringDelta.Compute(oldValue, newValue);

        d.Inserted.Should().Be("\U0001F510");
        d.RemovedLength.Should().Be(2);
        d.Apply(oldValue).Should().Be(newValue);
    }

    /// <summary>Two emoji that share their LOW surrogate (🤨 <c>\uD83E\uDD28</c>, 🔨
    /// <c>\uD83D\uDD28</c>): the code-unit suffix match starts one unit into the character, and
    /// the splice must still carry the whole replacement.</summary>
    [Fact]
    public void Compute_SharedLowSurrogate_KeepsSuffixWhole()
    {
        const string oldValue = "x\U0001F928y";
        const string newValue = "x\U0001F528y";

        var d = StringDelta.Compute(oldValue, newValue);

        d.Inserted.Should().Be("\U0001F528");
        d.RemovedLength.Should().Be(2);
        d.Apply(oldValue).Should().Be(newValue);
    }

    private static bool IsWholeText(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
                return false;
        }
        return true;
    }

    [Fact]
    public void Compute_InsertInMiddle_OnlyEncodesInsertedSpan()
    {
        // Insert "brave " before "new". Prefix = "hello ", suffix = "new world".
        var d = StringDelta.Compute("hello new world", "hello brave new world");
        d.Start.Should().Be(6);
        d.RemovedLength.Should().Be(0);
        d.Inserted.Should().Be("brave ");
        d.Apply("hello new world").Should().Be("hello brave new world");
    }

    [Fact]
    public void Compute_Append_StartAtEnd()
    {
        var d = StringDelta.Compute("hello", "hello world");
        d.Start.Should().Be(5);
        d.RemovedLength.Should().Be(0);
        d.Inserted.Should().Be(" world");
    }

    [Fact]
    public void Compute_Delete_NoInsert()
    {
        var d = StringDelta.Compute("hello brave new world", "hello new world");
        d.Inserted.Should().BeEmpty();
        d.RemovedLength.Should().Be(6); // "brave "
        d.Apply("hello brave new world").Should().Be("hello new world");
    }

    [Fact]
    public void Compute_Replace_RemovesAndInserts()
    {
        var d = StringDelta.Compute("the quick fox", "the slow fox");
        d.Apply("the quick fox").Should().Be("the slow fox");
        // delta is the changed middle, not the whole string
        d.Start.Should().Be(4);
        d.Inserted.Should().Be("slow");
        d.RemovedLength.Should().Be(5); // "quick"
    }

    [Fact]
    public void Compute_BigString_OneEdit_DeltaIsSmall()
    {
        var big = new string('a', 10_000);
        var edited = big.Insert(5_000, "INSERTED");
        var d = StringDelta.Compute(big, edited);
        d.Start.Should().Be(5_000);
        d.RemovedLength.Should().Be(0);
        d.Inserted.Should().Be("INSERTED");
        // The delta carries 8 chars, not 10_008 — that's the whole point.
        d.Inserted.Length.Should().BeLessThan(20);
        d.Apply(big).Should().Be(edited);
    }

    [Fact]
    public void Apply_RoundTripsCompute()
    {
        foreach (var (a, b) in new[]
                 {
                     ("", "x"), ("x", ""), ("abc", "abXc"), ("abc", "Xbc"),
                     ("abc", "abcX"), ("hello", "world"), ("aXa", "aYa"),
                 })
        {
            StringDelta.Compute(a, b).Apply(a).Should().Be(b, $"compute({a}->{b}).apply({a})");
        }
    }

    [Fact]
    public void ApplyAll_DisjointEdits_BothLand()
    {
        // Two writers edit the SAME base at different spots.
        const string baseText = "The quick brown fox jumps";
        var d1 = StringDelta.Compute(baseText, "The VERY quick brown fox jumps"); // edit near start
        var d2 = StringDelta.Compute(baseText, "The quick brown fox leaps");       // edit near end

        StringDelta.Overlaps(d1, d2).Should().BeFalse();
        var merged = StringDelta.ApplyAll(baseText, [d1, d2]);
        merged.Should().Be("The VERY quick brown fox leaps");
    }

    [Fact]
    public void ApplyAll_OverlappingEdits_Throws()
    {
        const string baseText = "abcdef";
        var d1 = StringDelta.Compute(baseText, "abXYZef"); // replaces "cd"
        var d2 = StringDelta.Compute(baseText, "abcQRf");  // replaces "de"
        StringDelta.Overlaps(d1, d2).Should().BeTrue();
        Assert.Throws<System.InvalidOperationException>(
            () => StringDelta.ApplyAll(baseText, [d1, d2]));
    }

    [Fact]
    public void Overlaps_DistinctInsertPoints_NotOverlapping()
    {
        var d1 = new StringDelta(2, 0, "X");
        var d2 = new StringDelta(5, 0, "Y");
        StringDelta.Overlaps(d1, d2).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_SameInsertPoint_Overlapping()
    {
        var d1 = new StringDelta(3, 0, "X");
        var d2 = new StringDelta(3, 0, "Y");
        StringDelta.Overlaps(d1, d2).Should().BeTrue();
    }
}
