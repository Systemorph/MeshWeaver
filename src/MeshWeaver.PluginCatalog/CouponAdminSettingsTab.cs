using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The platform-admin "Coupons" Settings tab — the administration surface for the Store's typed
/// coupons at <c>Admin/Coupons/{CODE}</c>. Lists every coupon LIVE (what it unlocks, its price,
/// validity window and redemption tally), links each to its node page (where the Store/Coupon
/// type's own Edit / Delete actions live), and offers the standard Create flow for a new code.
///
/// <para>The coupon TYPE is a dynamic node type (the Store plugin's <c>Store/Coupon</c>), so this
/// tab reads content by SHAPE — typed, <c>JsonElement</c> or <c>JsonNode</c>, whatever arrives —
/// and never references the plugin's compiled types.</para>
///
/// <para>Gated exactly like the Plugin Catalog tab — visible only when the viewer is a global
/// admin AND on their own settings page — and grouped under "Administration" beside it.</para>
/// </summary>
public static class CouponAdminSettingsTab
{
    /// <summary>The settings-menu item id for the Coupons tab.</summary>
    public const string TabId = "Coupons";

    /// <summary>Where the Store's coupons live — the node id is the code.</summary>
    public const string CouponsNamespace = "Admin/Coupons";

    /// <summary>Registers the Coupons settings tab provider (global admins only).</summary>
    public static MessageHubConfiguration AddCouponAdminSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        // Same home as the Global Administration tab: the admin's own settings page.
        var hubPath = host.Hub.Address.ToString();
        var nodeOwnerId = hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
            ? hubPath["User/".Length..]
            : hubPath;
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Coupons",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.TicketDiagonal(),
            GroupIcon: FluentIcons.Shield(),
            Order: 330,
            Keywords: ["coupons", "coupon codes", "discount", "redeem", "redemption", "store",
                "entitlement", "unlock", "voucher", "promo"]);

        // Wait for the POSITIVE admin confirmation with a bounded timeout; StartWith(none) so the
        // menu renders immediately (mirrors the Global Administration tab).
        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    // ── Row model (pure — unit-tested) ─────────────────────────────────────────

    /// <summary>Plain row record bound into the coupon <see cref="DataGridControl"/>.</summary>
    public sealed record CouponRow
    {
        /// <summary>The coupon code (the node id).</summary>
        public string Code { get; init; } = string.Empty;
        /// <summary>
        /// What the coupon unlocks: the package list comma-joined; the <c>grantsAll</c> label when
        /// the coupon covers everything; the empty-list label otherwise.
        ///
        /// <para>🚨 It used to read <b>"any plugin"</b> for an empty list, which described
        /// behaviour the Store never had. An empty list means the coupon may be REDEEMED anywhere
        /// and grants only the package it was redeemed ON — the opposite of "unlocks any plugin"
        /// (Systemorph/MeshWeaver.Plugins#321). "Unlocks everything" is the coupon's own
        /// <c>grantsAll</c> flag, and now has its own label.</para>
        /// </summary>
        public string Unlocks { get; init; } = string.Empty;
        /// <summary>The effective price: "free" when the coupon carries none (or 0), else the
        /// discounted amount with currency.</summary>
        public string Price { get; init; } = string.Empty;
        /// <summary>The validity window, or "always" when unbounded.</summary>
        public string Valid { get; init; } = string.Empty;
        /// <summary>Redemption tally: "used / cap" with a capped budget, else just the count.</summary>
        public string Redeemed { get; init; } = string.Empty;
        /// <summary>The admin notes (may be empty).</summary>
        public string Notes { get; init; } = string.Empty;
    }

    /// <summary>
    /// Projects a coupon node into its grid row — by SHAPE, whatever form the content arrives in
    /// (the Store/Coupon type is dynamically compiled, so this assembly never sees it typed as its
    /// own class; a foreign hub may still deliver it typed, a query a <c>JsonElement</c>, a
    /// builder a <c>JsonNode</c>). Pure.
    /// </summary>
    /// <param name="localize">
    /// Resolves the two <see cref="CouponRow.Unlocks"/> labels for the viewer's language. Optional
    /// so the projection stays a pure function callable without a host (its tests do exactly that);
    /// callers that HAVE a host must pass <c>host.Localize</c>, or a German admin reads English.
    /// </param>
    public static CouponRow ToRow(
        MeshNode node, JsonSerializerOptions options, Func<string, string>? localize = null)
    {
        var content = ElementOf(node, options);
        var plugins = content is { } c && c.TryGetProperty("plugins", out var p)
                      && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToArray()
            : [];
        var grantsAll = content is { } g && g.TryGetProperty("grantsAll", out var ga)
                        && ga.ValueKind == JsonValueKind.True;
        var price = ReadDecimal(content, "price");
        var currency = ReadString(content, "currency") ?? "CHF";
        var validFrom = ReadString(content, "validFrom");
        var validUntil = ReadString(content, "validUntil");
        var redeemed = ReadDecimal(content, "redeemed") ?? 0m;
        var cap = ReadDecimal(content, "maxRedemptions");

        return new CouponRow
        {
            Code = node.Id,
            // grantsAll wins the cell: it is the one thing that unlocks more than the list names.
            // An empty list is NOT "any plugin" — see CouponRow.Unlocks.
            Unlocks = grantsAll
                ? Label(localize, "ui.couponUnlocksEverything", "everything")
                : plugins.Length == 0
                    ? Label(localize, "ui.couponUnlocksRedeemedOn", "the package it is used on")
                    : string.Join(", ", plugins),
            Price = price is null or 0m ? "free" : $"{currency} {price:0.##}",
            Valid = (validFrom, validUntil) switch
            {
                (null, null) => "always",
                (null, { } u) => $"until {DatePart(u)}",
                ({ } f, null) => $"from {DatePart(f)}",
                ({ } f, { } u) => $"{DatePart(f)} – {DatePart(u)}",
            },
            Redeemed = cap is { } budget ? $"{redeemed:0} / {budget:0}" : $"{redeemed:0}",
            Notes = ReadString(content, "notes") ?? "",
        };
    }

    // The localized label, or the English fallback when this projection was called without a host
    // (its unit tests). Never CultureInfo.CurrentUICulture — a layout-area render hops the hub
    // scheduler and an ambient culture does not survive it.
    private static string Label(Func<string, string>? localize, string key, string english) =>
        localize?.Invoke(key) is { Length: > 0 } localized && localized != key ? localized : english;

    private static string DatePart(string isoTimestamp) =>
        isoTimestamp.Length >= 10 ? isoTimestamp[..10] : isoTimestamp;

    private static string? ReadString(JsonElement? content, string name) =>
        content is { } c && c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement? content, string name) =>
        content is { } c && c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal()
            : null;

    /// <summary>
    /// The node's content as a <see cref="JsonElement"/> object, whatever shape it arrived in.
    /// Typed content serializes with its CONCRETE runtime type — never the <c>object</c> overload,
    /// whose polymorphic path adopts foreign types into the registry as a read side effect.
    /// </summary>
    private static JsonElement? ElementOf(MeshNode node, JsonSerializerOptions options)
    {
        var element = node.Content switch
        {
            null => (JsonElement?)null,
            JsonElement je => je,
            System.Text.Json.Nodes.JsonNode jn => JsonSerializer.SerializeToElement(jn, options),
            var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options),
        };
        return element is { ValueKind: JsonValueKind.Object } ? element : null;
    }

    // ── Tab content ────────────────────────────────────────────────────────────

    private static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack
            .WithView(Controls.Markdown(
                "Typed coupon codes at `Admin/Coupons/{CODE}`. A free coupon unlocks its whole " +
                "plugin list in one redemption; a priced one applies at payment. Open a coupon " +
                "to edit or delete it with the standard node actions."))
            .WithView(Controls.Button(host.Localize("ui.newCoupon"))
                .WithAppearance(Appearance.Accent)
                .WithNavigateToHref("/Store/Coupon/Create?type=Store%2FCoupon"), "NewCoupon")
            .WithView((h, _) => LiveCouponList(h), "CouponList");
        return stack;
    }

    /// <summary>
    /// The LIVE coupon list: accumulates the chunked query over <see cref="CouponsNamespace"/>
    /// (creates, edits and deletes all land) and re-renders the grid per snapshot. The viewer is
    /// a confirmed global admin, so the query runs under their own identity — no impersonation.
    /// </summary>
    private static IObservable<UiControl?> LiveCouponList(LayoutAreaHost host)
    {
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("ui.mdMeshUnavailable")));
        var options = host.Hub.JsonSerializerOptions;

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{CouponsNamespace} scope:children"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, Accumulate)
            // Let the chunked first snapshot settle instead of re-rendering per chunk.
            .Throttle(TimeSpan.FromMilliseconds(300))
            .StartWith(ImmutableDictionary<string, MeshNode>.Empty)
            .Select(map => (UiControl?)BuildGrid(host, map, options));
    }

    /// <summary>One accumulation step over the chunked query — pure: Reset restarts, Removed
    /// deletes, everything else upserts by path.</summary>
    public static ImmutableDictionary<string, MeshNode> Accumulate(
        ImmutableDictionary<string, MeshNode> map, QueryResultChange<MeshNode> change)
    {
        if (change.ChangeType == QueryChangeType.Reset)
            map = ImmutableDictionary<string, MeshNode>.Empty;
        foreach (var item in change.Items)
            map = change.ChangeType == QueryChangeType.Removed
                ? map.Remove(item.Path)
                : map.SetItem(item.Path, item);
        return map;
    }

    private static UiControl BuildGrid(
        LayoutAreaHost host, ImmutableDictionary<string, MeshNode> coupons, JsonSerializerOptions options)
    {
        if (coupons.IsEmpty)
            return Controls.Markdown(host.Localize("ui.mdNoCoupons"));

        var rows = coupons.Values
            .OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(n => ToRow(n, options, key => host.Localize(key)))
            .ToImmutableArray();

        // Rows bind INLINE — a per-render UpdateData under a fresh id would accumulate orphaned
        // data entries for the host's lifetime (each snapshot re-renders this grid).
        var grid = Controls.DataGrid(rows)
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Code).ToCamelCase() }.WithTitle("Code"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Unlocks).ToCamelCase() }.WithTitle("Unlocks"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Price).ToCamelCase() }.WithTitle("Price"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Valid).ToCamelCase() }.WithTitle("Valid"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Redeemed).ToCamelCase() }.WithTitle("Redeemed"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(CouponRow.Notes).ToCamelCase() }.WithTitle("Notes"))
            .Resizable();

        // DataGrid has no row-click — one Open button per coupon navigates to its node page,
        // where the Store/Coupon type's own Edit / Delete actions live.
        var openRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap; margin-top: 8px;");
        foreach (var row in rows)
            openRow = openRow.WithView(Controls.Button(row.Code)
                .WithAppearance(Appearance.Outline)
                .WithNavigateToHref($"/{CouponsNamespace}/{row.Code}"));

        return Controls.Stack
            .WithView(grid)
            .WithView(Controls.Markdown(host.Localize("ui.openCoupon")))
            .WithView(openRow);
    }
}
