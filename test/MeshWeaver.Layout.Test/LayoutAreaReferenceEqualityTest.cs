#pragma warning disable CS1591

using System.Text.Json;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins <see cref="LayoutAreaReference"/> equality across a JSON round-trip of its <c>Id</c>.
///
/// 🚨 Regression: <c>Id</c> is an <c>object?</c> that serializes to the client and back, so a
/// deserialized <c>Id</c> is a <see cref="JsonElement"/>. The old <c>Equals(Id, other.Id)</c> made a
/// freshly-built reference (string <c>Id</c>) never equal the deserialized one (JsonElement <c>Id</c>),
/// so <c>LayoutAreaView</c>'s <c>!AreaStream.Reference.Equals(ViewModel.Reference)</c> was perpetually
/// true → it disposed + re-subscribed the area on every render (the "stuck subscribing…" / render
/// storm). Equality must normalize the JsonElement scalar.
/// </summary>
public class LayoutAreaReferenceEqualityTest
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void StringId_EqualsJsonElementStringId()
    {
        var fresh = new LayoutAreaReference("Overview") { Id = "v12" };
        var deserialized = new LayoutAreaReference("Overview") { Id = Json("\"v12\"") };

        Assert.Equal(fresh, deserialized);
        Assert.Equal(fresh.GetHashCode(), deserialized.GetHashCode());
    }

    [Fact]
    public void TwoJsonElementIds_SameContent_AreEqual()
    {
        // Two independent parses → distinct JsonElements with no structural equality of their own.
        var a = new LayoutAreaReference("VersionDiff") { Id = Json("\"abc\"") };
        var b = new LayoutAreaReference("VersionDiff") { Id = Json("\"abc\"") };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentId_NotEqual()
    {
        var a = new LayoutAreaReference("Overview") { Id = "v12" };
        var b = new LayoutAreaReference("Overview") { Id = Json("\"v13\"") };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NullId_BothSides_AreEqual()
    {
        Assert.Equal(new LayoutAreaReference("Overview"), new LayoutAreaReference("Overview"));
    }

    [Fact]
    public void DifferentArea_NotEqual()
    {
        Assert.NotEqual(new LayoutAreaReference("Overview"), new LayoutAreaReference("FullHeader"));
    }
}
