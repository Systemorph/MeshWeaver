using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Json.Test;

/// <summary>
/// RFC 6901 — parse, escape, unescape, resolve, round-trip. The escaping rules are the reason
/// this type exists: json-everything's applier used the ESCAPED segment as a property name, which
/// is why <c>JsonSynchronizationStream</c> had to hand-roll its own applier (#1231).
/// </summary>
public class JsonPointerTest
{
    // ---- parsing ------------------------------------------------------------------

    [Theory]
    [InlineData("", 0)]
    [InlineData("/", 1)]
    [InlineData("/a", 1)]
    [InlineData("/a/b", 2)]
    [InlineData("/a/b/c", 3)]
    [InlineData("//", 2)]
    [InlineData("/a//b", 3)]
    [InlineData("/a~1b", 1)]
    [InlineData("/a~0b", 1)]
    [InlineData("/x/y/z/0/-/~0/~1", 7)]
    public void Parse_CountsSegments(string pointer, int expected)
        => Assert.Equal(expected, JsonPointer.Parse(pointer).SegmentCount);

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/a")]
    [InlineData("/a/b")]
    [InlineData("/a~1b")]
    [InlineData("/a~0b")]
    [InlineData("/a~01b")]
    [InlineData("/a~00b")]
    [InlineData("/~0~1")]
    [InlineData("/~1~0")]
    [InlineData("/ ")]
    [InlineData("/a b")]
    [InlineData("/äöü")]
    [InlineData("/k😀")]
    [InlineData("/\"quoted\"")]
    [InlineData("/a\\b")]
    [InlineData("/-")]
    [InlineData("/0")]
    [InlineData("/00")]
    [InlineData("/-1")]
    public void Parse_RoundTripsVerbatim(string pointer)
        => Assert.Equal(pointer, JsonPointer.Parse(pointer).ToString());

    [Theory]
    [InlineData("a")]
    [InlineData("a/b")]
    [InlineData("abc")]
    [InlineData("~")]
    [InlineData("/~")]
    [InlineData("/~2")]
    [InlineData("/~x")]
    [InlineData("/a~")]
    public void Parse_RejectsMalformed(string pointer)
    {
        Assert.Throws<PointerParseException>(() => JsonPointer.Parse(pointer));
        Assert.False(JsonPointer.TryParse(pointer, out _));
    }

    [Fact]
    public void Parse_NullThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => JsonPointer.Parse((string)null!));

    [Theory]
    [InlineData("#", "")]
    [InlineData("#/a", "/a")]
    [InlineData("#/a~1b", "/a~1b")]
    [InlineData("#/a%20b", "/a b")]
    public void Parse_AcceptsUriFragmentForm(string input, string expected)
        => Assert.Equal(expected, JsonPointer.Parse(input).ToString());

    [Fact]
    public void Empty_IsTheRootPointer()
    {
        Assert.Equal(string.Empty, JsonPointer.Empty.ToString());
        Assert.Equal(0, JsonPointer.Empty.SegmentCount);
        Assert.Equal(JsonPointer.Empty, default(JsonPointer));
        Assert.Equal(JsonPointer.Empty, JsonPointer.Parse(""));
    }

    // ---- segments -----------------------------------------------------------------

    /// <summary>
    /// 🚨 A segment reads back ESCAPED, not decoded. Callers in MeshWeaver.Data rely on this
    /// (they decode themselves), so it is contract, not incidental.
    /// </summary>
    [Theory]
    [InlineData("/a~1b", 0, "a~1b", "a/b")]
    [InlineData("/a~0b", 0, "a~0b", "a~b")]
    [InlineData("/a~01b", 0, "a~01b", "a~1b")]
    [InlineData("/a~00b", 0, "a~00b", "a~0b")]
    [InlineData("/~1", 0, "~1", "/")]
    [InlineData("/~0", 0, "~0", "~")]
    [InlineData("/~0~1", 0, "~0~1", "~/")]
    [InlineData("/~1~0", 0, "~1~0", "/~")]
    [InlineData("/plain", 0, "plain", "plain")]
    [InlineData("/", 0, "", "")]
    [InlineData("/a/b", 1, "b", "b")]
    [InlineData("/a//b", 1, "", "")]
    [InlineData("/a//b", 2, "b", "b")]
    public void GetSegment_KeepsEscapingAndDecodesOnRequest(string pointer, int index, string escaped, string decoded)
    {
        var segment = JsonPointer.Parse(pointer).GetSegment(index);
        Assert.Equal(escaped, segment.ToString());
        Assert.Equal(decoded, segment.Decode());
    }

    /// <summary>
    /// The order trap: <c>~01</c> decodes to <c>~1</c>. A two-pass
    /// <c>Replace("~0","~").Replace("~1","/")</c> yields <c>/</c> — wrong.
    /// </summary>
    [Theory]
    [InlineData("~01", "~1")]
    [InlineData("~00", "~0")]
    [InlineData("~10", "/0")]
    [InlineData("~11", "/1")]
    [InlineData("~0~1", "~/")]
    [InlineData("~1~0", "/~")]
    [InlineData("~0~0", "~~")]
    [InlineData("a~1b~0c", "a/b~c")]
    public void Decode_IsSinglePass(string escaped, string expected)
        => Assert.Equal(expected, JsonPointerSegment.Decode(escaped));

    [Theory]
    [InlineData("a/b", "a~1b")]
    [InlineData("a~b", "a~0b")]
    [InlineData("a~1b", "a~01b")]
    [InlineData("~", "~0")]
    [InlineData("/", "~1")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("\"acme/Docs/One\"", "\"acme~1Docs~1One\"")]
    public void Escape_Decode_RoundTrip(string raw, string escaped)
    {
        Assert.Equal(escaped, JsonPointer.Escape(raw));
        Assert.Equal(raw, JsonPointerSegment.Decode(escaped));
    }

    [Fact]
    public void GetSegment_OutOfRangeThrows()
    {
        var pointer = JsonPointer.Parse("/a/b");
        Assert.Throws<ArgumentOutOfRangeException>(() => pointer.GetSegment(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pointer.GetSegment(2));
        Assert.False(pointer.TryGetSegment(2, out _));
        Assert.True(pointer.TryGetSegment(1, out var segment));
        Assert.Equal("b", segment.ToString());
    }

    /// <summary>
    /// Segment comparison decodes the SEGMENT while reading the probe as-is, so it matches a
    /// property name given in its real (unescaped) form.
    /// <para>
    /// 🚨 It does NOT match an already-escaped probe: <c>/a~1b</c> vs <c>"a~1b"</c> is <c>false</c>,
    /// because the segment's <c>~1</c> is consumed as one character (<c>/</c>) while the probe's is
    /// read as two, and the two sides then fall out of step. That asymmetry is inherited behaviour,
    /// verified identical to json-everything by the differential run — do not "fix" it here without
    /// checking what depends on it. To compare escaped forms, compare the pointers instead.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/a~1b", "a/b", true)]
    [InlineData("/a~1b", "a~1b", false)]
    [InlineData("/a~1b", "a~b", false)]
    [InlineData("/a~0b", "a~b", true)]
    [InlineData("/a~0b", "a~0b", false)]
    [InlineData("/plain", "plain", true)]
    [InlineData("/plain", "other", false)]
    [InlineData("/", "", true)]
    [InlineData("/a", "", false)]
    [InlineData("/", "a", false)]
    public void Segment_ComparesAcrossEscaping(string pointer, string probe, bool expected)
        => Assert.Equal(expected, JsonPointer.Parse(pointer).GetSegment(0) == probe);

    // ---- construction ---------------------------------------------------------------

    [Theory]
    [InlineData("a", "/a")]
    [InlineData("a/b", "/a~1b")]
    [InlineData("a~b", "/a~0b")]
    [InlineData("a~1b", "/a~01b")]
    [InlineData("~", "/~0")]
    [InlineData("/", "/~1")]
    [InlineData("", "/")]
    [InlineData("-", "/-")]
    [InlineData("0", "/0")]
    [InlineData("äöü", "/äöü")]
    [InlineData("\"quoted\"", "/\"quoted\"")]
    public void Create_EscapesTheSegment(string segment, string expected)
    {
        Assert.Equal(expected, JsonPointer.Create(segment).ToString());
        Assert.Equal(1, JsonPointer.Create(segment).SegmentCount);
    }

    [Fact]
    public void Create_MultipleSegments()
    {
        Assert.Equal("", JsonPointer.Create().ToString());
        Assert.Equal("/a/b", JsonPointer.Create("a", "b").ToString());
        Assert.Equal("//", JsonPointer.Create("", "").ToString());
        Assert.Equal("/MeshWeaver.Mesh.Contract.MeshNode/\"my~1id~0x\"",
            JsonPointer.Create("MeshWeaver.Mesh.Contract.MeshNode", "\"my/id~x\"").ToString());
        Assert.Equal("/a/b~1c/d~0e//-/0",
            JsonPointer.Create(["a", "b/c", "d~e", "", "-", "0"]).ToString());
        Assert.Equal(6, JsonPointer.Create(["a", "b/c", "d~e", "", "-", "0"]).SegmentCount);
        Assert.Equal("/7", JsonPointer.Create(7).ToString());
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("", "/a", "/a")]
    [InlineData("/a", "", "/a")]
    [InlineData("/a", "/b", "/a/b")]
    [InlineData("/a/b", "/c/d", "/a/b/c/d")]
    [InlineData("/a~1b", "/c", "/a~1b/c")]
    [InlineData("/", "/a", "//a")]
    [InlineData("/a", "/", "/a/")]
    public void Combine_ConcatenatesEscapedText(string a, string b, string expected)
    {
        var combined = JsonPointer.Parse(a).Combine(JsonPointer.Parse(b));
        Assert.Equal(expected, combined.ToString());
        Assert.Equal(JsonPointer.Parse(a).SegmentCount + JsonPointer.Parse(b).SegmentCount, combined.SegmentCount);
    }

    [Fact]
    public void Combine_WithSegmentEscapes()
    {
        Assert.Equal("/a/b~1c", JsonPointer.Parse("/a").Combine("b/c").ToString());
        Assert.Equal("/a/3", JsonPointer.Parse("/a").Combine(3).ToString());
        Assert.Equal(2, JsonPointer.Parse("/a").Combine("b/c").SegmentCount);
    }

    // ---- resolution ---------------------------------------------------------------

    private static readonly JsonElement Document = JsonDocument.Parse(GoldenFixtures.Text("evalDocument")).RootElement;

    [Theory]
    [InlineData("", true)]
    [InlineData("/a/b/c", true)]
    [InlineData("/a/0", true)]
    [InlineData("/a/", true)]
    [InlineData("/0", true)]
    [InlineData("//", true)]
    [InlineData("/-", true)]
    [InlineData("/a~1b", true)]
    [InlineData("/a~0b", true)]
    [InlineData("/a~01b", true)]
    [InlineData("/~1", true)]
    [InlineData("/~0", true)]
    [InlineData("/äöü", true)]
    [InlineData("/k😀", true)]
    [InlineData("/\"quoted\"", true)]
    [InlineData("/a\\b", true)]
    [InlineData("/ ", true)]
    [InlineData("/missing", false)]
    [InlineData("/a/b/c/d", false)]
    [InlineData("/arr/0/x", false)]
    public void Evaluate_ResolvesEscapedKeys(string pointer, bool found)
        => Assert.Equal(found, JsonPointer.Parse(pointer).Evaluate(Document).HasValue);

    /// <summary>
    /// Array indexing: no leading zeros, no negatives, out of range misses — and <c>-</c>
    /// resolves to the LAST element (the behaviour the clients were built against, not the
    /// strict RFC "one past the end" reading).
    /// </summary>
    [Theory]
    [InlineData("/arr/0", "10")]
    [InlineData("/arr/1", "20")]
    [InlineData("/arr/2", "30")]
    [InlineData("/arr/-", "30")]
    [InlineData("/arr/3", null)]
    [InlineData("/arr/01", null)]
    [InlineData("/arr/-1", null)]
    [InlineData("/arr/x", null)]
    [InlineData("/arr/", null)]
    public void Evaluate_ArrayIndexRules(string pointer, string? expected)
    {
        var result = JsonPointer.Parse(pointer).Evaluate(Document);
        Assert.Equal(expected, result?.GetRawText());
    }

    [Fact]
    public void TryEvaluate_MirrorsEvaluateOverNodes()
    {
        var node = JsonNode.Parse(GoldenFixtures.Text("evalDocument"));
        foreach (var row in GoldenFixtures.Section("pointerEvaluate"))
        {
            var pointer = row.GetProperty("pointer").GetString()!;
            var found = row.GetProperty("found").GetBoolean();
            Assert.Equal(found, JsonPointer.Parse(pointer).TryEvaluate(node, out _));
        }
    }

    [Fact]
    public void Evaluate_ThroughScalarMisses()
    {
        var element = JsonDocument.Parse("""{"a":1}""").RootElement;
        Assert.Null(JsonPointer.Parse("/a/b").Evaluate(element));
        Assert.False(JsonPointer.Parse("/a/b").TryEvaluate(JsonNode.Parse("""{"a":1}"""), out _));
    }

    // ---- equality + serialization ---------------------------------------------------

    [Fact]
    public void Equality_IsOrdinalOverTheEscapedText()
    {
        Assert.Equal(JsonPointer.Parse("/a~1b"), JsonPointer.Create("a/b"));
        Assert.True(JsonPointer.Parse("/a") == JsonPointer.Create("a"));
        Assert.True(JsonPointer.Parse("/a") != JsonPointer.Parse("/b"));
        // Same TARGET, different ENCODING — deliberately NOT equal, matching the prior behaviour.
        Assert.NotEqual(JsonPointer.Parse("/a~1b"), JsonPointer.Parse("/a"));
        Assert.Equal(JsonPointer.Parse("/a").GetHashCode(), JsonPointer.Create("a").GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("/a~1b/c~0d")]
    public void Serializes_AsItsRfc6901String(string pointer)
    {
        var parsed = JsonPointer.Parse(pointer);
        var json = JsonSerializer.Serialize(parsed);
        Assert.Equal(JsonSerializer.Serialize(pointer), json);
        Assert.Equal(parsed, JsonSerializer.Deserialize<JsonPointer>(json));
    }

    // ---- the captured oracle --------------------------------------------------------

    [Fact]
    public void MatchesGolden_Parse()
    {
        foreach (var row in GoldenFixtures.Section("pointerParse"))
        {
            var input = row.GetProperty("input").GetString()!;
            var pointer = JsonPointer.Parse(input);
            Assert.Equal(row.GetProperty("text").GetString(), pointer.ToString());
            Assert.Equal(row.GetProperty("segmentCount").GetInt32(), pointer.SegmentCount);
            var expected = row.GetProperty("segments").EnumerateArray().Select(s => s.GetString()).ToArray();
            var actual = Enumerable.Range(0, pointer.SegmentCount)
                .Select(i => JsonPointer.Parse(input).GetSegment(i).ToString()).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void MatchesGolden_Invalid()
    {
        foreach (var row in GoldenFixtures.Section("pointerInvalid"))
            Assert.False(JsonPointer.TryParse(row.GetString(), out _));
    }

    [Fact]
    public void MatchesGolden_Create()
    {
        foreach (var row in GoldenFixtures.Section("pointerCreate"))
        {
            var segments = row.GetProperty("segments").EnumerateArray().Select(s => s.GetString()!).ToArray();
            var pointer = segments.Length switch
            {
                0 => JsonPointer.Create(),
                1 => JsonPointer.Create(segments[0]),
                2 => JsonPointer.Create(segments[0], segments[1]),
                _ => JsonPointer.Create(segments)
            };
            Assert.Equal(row.GetProperty("text").GetString(), pointer.ToString());
        }
    }

    [Fact]
    public void MatchesGolden_Evaluate()
    {
        foreach (var row in GoldenFixtures.Section("pointerEvaluate"))
        {
            var pointer = row.GetProperty("pointer").GetString()!;
            var result = JsonPointer.Parse(pointer).Evaluate(Document);
            Assert.Equal(row.GetProperty("found").GetBoolean(), result.HasValue);
            var raw = row.GetProperty("rawValue");
            if (raw.ValueKind != JsonValueKind.Null)
                Assert.Equal(raw.GetString(), result!.Value.GetRawText());
        }
    }

    [Fact]
    public void MatchesGolden_Combine()
    {
        foreach (var row in GoldenFixtures.Section("pointerCombine"))
        {
            var combined = JsonPointer.Parse(row.GetProperty("a").GetString()!)
                .Combine(JsonPointer.Parse(row.GetProperty("b").GetString()!));
            Assert.Equal(row.GetProperty("text").GetString(), combined.ToString());
            Assert.Equal(row.GetProperty("segmentCount").GetInt32(), combined.SegmentCount);
        }
    }
}
