using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the Coupons administration tab's row projection and live-list accumulation. The coupon
/// type (<c>Store/Coupon</c>) is DYNAMIC — this assembly never sees it as a compiled class — so
/// <see cref="CouponAdminSettingsTab.ToRow"/> must read the content by shape, in every form it
/// arrives: a query's <c>JsonElement</c>, a builder's <c>JsonNode</c>, or absent.
/// </summary>
public class CouponAdminSettingsTabTest
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static MeshNode Coupon(string code, string json) =>
        new(code, CouponAdminSettingsTab.CouponsNamespace)
        {
            Name = code,
            NodeType = "Store/Coupon",
            Content = JsonDocument.Parse(json).RootElement.Clone(),
        };

    [Fact]
    public void ToRow_FreeMultiPluginCoupon_TheTypicalStoredShape()
    {
        var row = CouponAdminSettingsTab.ToRow(Coupon("UNLOCKALL",
            """
            {"$type":"CouponContent","currency":"CHF",
             "plugins":["Chess","Claims","Edu"],
             "redeemed":3,"redeemedBy":["a","b","c"],"redemptions":[],
             "notes":"unlock everything"}
            """), Options);
        Assert.Equal("UNLOCKALL", row.Code);
        Assert.Equal("Chess, Claims, Edu", row.Unlocks);
        Assert.Equal("free", row.Price);
        Assert.Equal("always", row.Valid);
        Assert.Equal("3", row.Redeemed);
        Assert.Equal("unlock everything", row.Notes);
    }

    [Fact]
    public void ToRow_PricedWindowedCappedCoupon()
    {
        var row = CouponAdminSettingsTab.ToRow(Coupon("HALFOFF",
            """
            {"$type":"CouponContent","currency":"CHF","price":450,
             "plugins":["DataModeling"],
             "validFrom":"2026-08-01T00:00:00+00:00","validUntil":"2026-09-01T00:00:00+00:00",
             "maxRedemptions":10,"redeemed":4}
            """), Options);
        Assert.Equal("CHF 450", row.Price);
        Assert.Equal("2026-08-01 – 2026-09-01", row.Valid);
        Assert.Equal("4 / 10", row.Redeemed);
    }

    [Fact]
    public void ToRow_EmptyAllowList_ReadsAsAnyPlugin_AndZeroPriceIsFree()
    {
        var row = CouponAdminSettingsTab.ToRow(Coupon("OPEN",
            """{"$type":"CouponContent","price":0,"validUntil":"2026-12-31T00:00:00+00:00"}"""), Options);
        Assert.Equal("any plugin", row.Unlocks);
        Assert.Equal("free", row.Price);
        Assert.Equal("until 2026-12-31", row.Valid);
        Assert.Equal("0", row.Redeemed);
        Assert.Equal("", row.Notes);
    }

    [Fact]
    public void ToRow_ReadsEveryContentShape()
    {
        // JsonNode (a builder's shape).
        var asNode = new MeshNode("N1", CouponAdminSettingsTab.CouponsNamespace)
        {
            NodeType = "Store/Coupon",
            Content = JsonNode.Parse("""{"plugins":["Chess"],"redeemed":1}""")!.AsObject(),
        };
        Assert.Equal("Chess", CouponAdminSettingsTab.ToRow(asNode, Options).Unlocks);

        // Absent / non-object content degrades to an empty row, never a throw.
        var empty = new MeshNode("N2", CouponAdminSettingsTab.CouponsNamespace)
            { NodeType = "Store/Coupon", Content = null };
        var row = CouponAdminSettingsTab.ToRow(empty, Options);
        Assert.Equal("N2", row.Code);
        Assert.Equal("any plugin", row.Unlocks);
        Assert.Equal("free", row.Price);
    }

    [Fact]
    public void Accumulate_UpsertsRemovesAndResets()
    {
        var a = Coupon("A", """{"plugins":[]}""");
        var b = Coupon("B", """{"plugins":[]}""");

        var map = CouponAdminSettingsTab.Accumulate(
            ImmutableDictionary<string, MeshNode>.Empty,
            new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, Items = [a, b] });
        Assert.Equal(2, map.Count);

        map = CouponAdminSettingsTab.Accumulate(map,
            new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Removed, Items = [a] });
        Assert.Single(map);
        Assert.True(map.ContainsKey(b.Path));

        map = CouponAdminSettingsTab.Accumulate(map,
            new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Reset, Items = [a] });
        Assert.Single(map);
        Assert.True(map.ContainsKey(a.Path));
    }
}
