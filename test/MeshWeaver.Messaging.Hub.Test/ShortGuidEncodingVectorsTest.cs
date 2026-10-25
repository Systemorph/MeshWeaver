using System;
using MeshWeaver.ShortGuid;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// ShortGuid ids are persisted, so their wire format is frozen. <see cref="GuidExtensions"/> used to
/// wrap the <c>CSharpVitamins.ShortGuid</c> 2.0.0 package; it is now implemented in-house. The vectors
/// below were produced BY THAT PACKAGE (encode output, and the decode outcome — value or exception
/// type — for valid and invalid input) and were checked against the in-house implementation over one
/// million random Guids and two hundred thousand random 22-character inputs before the package was
/// removed. Any change here is a breaking change to stored data.
/// </summary>
public class ShortGuidEncodingVectorsTest
{
    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "4AQlP4lP0xGaDAMF6CwzAQ")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "_____________________w")]
    [InlineData("00000000-0000-0000-0000-000000000001", "AAAAAAAAAAAAAAAAAAAAAQ")]
    [InlineData("fbffffff-ffff-ffff-ffff-ffffffffffff", "____-________________w")]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "ELina62d0RGAtADAT9QwyA")]
    [InlineData("01890a5d-ac96-774b-bcce-b302099a8057", "XQqJAZasS3e8zrMCCZqAVw")]
    public void EncodesAndDecodesToTheFrozenVector(string guid, string shortGuid)
    {
        var g = Guid.Parse(guid);
        Assert.Equal(shortGuid, g.AsString());
        Assert.Equal(g, shortGuid.AsGuid());
    }

    [Fact]
    public void EmptyMapsToEmptyBothWays()
    {
        Assert.Equal(string.Empty, Guid.Empty.AsString());
        Assert.Equal(Guid.Empty, string.Empty.AsGuid());
        Assert.Equal(Guid.Empty, ((string)null!).AsGuid());
    }

    [Fact]
    public void RandomGuidsRoundTripAs22UrlSafeCharacters()
    {
        var random = new Random(4711);
        var bytes = new byte[16];
        for (var i = 0; i < 10_000; i++)
        {
            random.NextBytes(bytes);
            var g = new Guid(bytes);
            var s = g.AsString();
            Assert.Matches("^[A-Za-z0-9_-]{22}$", s);
            Assert.Equal(g, s.AsGuid());
        }
    }

    [Theory]
    // a 36-character Guid string is NOT accepted — only the 22-character form
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("short")]
    [InlineData("4AQlP4lP0xGaDAMF6CwzAQ==")]
    // whitespace is skipped by the Base64 decoder, leaving fewer than 16 bytes
    [InlineData("AAAAAAAAAAAAAAAAAA    ")]
    public void RejectsWithArgumentException(string input)
    {
        // Assert.Throws matches the EXACT type, as the package threw it.
        Assert.Throws<ArgumentException>(() => input.AsGuid());
    }

    [Theory]
    // standard-alphabet '+' / '/' decode but fail the strict round trip
    [InlineData("____+________________w")]
    // non-canonical trailing bits fail the strict round trip
    [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("4AQlP4lP0xGaDAMF6CwzAB")]
    // not Base64 at all
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA  ")]
    public void RejectsWithFormatException(string input)
    {
        Assert.Throws<FormatException>(() => input.AsGuid());
    }
}
