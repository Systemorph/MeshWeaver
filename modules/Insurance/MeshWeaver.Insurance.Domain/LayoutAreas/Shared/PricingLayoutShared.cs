using MeshWeaver.Layout;

namespace MeshWeaver.Insurance.Domain.LayoutAreas.Shared;

public static class PricingLayoutShared
{
    public static UiControl BuildToolbar(string pricingId, string active)
    {
        string Item(string key, string icon, string text)
        {
            var href = $"/pricing/{pricingId}/{key}";
            var activeClass = key == active ? "font-weight:600;text-decoration:underline;" : "opacity:.75;";
            return $"<a style='display:flex;gap:4px;align-items:center;padding:4px 10px;border-radius:4px;text-decoration:none;{activeClass}' href='{href}'><span>{icon}</span><span>{text}</span></a>";
        }

        var html = $@"<nav style='display:flex;gap:4px;border-bottom:1px solid #ddd;margin:0 0 12px 0;padding:4px 0;flex-wrap:wrap'>
{Item("Overview", "🧾", "Overview")}
{Item("Submission", "📎", "Submission")}
{Item("PropertyRisks", "📄", "Risks")}
{Item("RiskMap", "🗺️", "Map")}
{Item("Structure", "🏦", "Reinsurance")}
{Item("ImportConfigs", "⚙️", "Import")}
</nav>";

        return Controls.Html(html);
    }
}
