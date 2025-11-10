using System.Reactive.Linq;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying individual pricing overview.
/// </summary>
public static class PricingOverviewLayoutArea
{
    /// <summary>
    /// Renders the overview for a specific pricing.
    /// </summary>
    public static IObservable<UiControl> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;

        return host.Workspace.GetStream<Pricing>()!
            .Select(pricings =>
            {
                var pricing = pricings?.FirstOrDefault();
                return pricing != null
                    ? Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Overview"))
                        .WithView(Controls.Markdown(RenderPricingOverview(pricing)))
                    : Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Overview"))
                        .WithView(Controls.Markdown($"# Pricing Overview\n\n*Pricing '{pricingId}' not found.*"));
            })
            .StartWith(Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Overview"))
                .WithView(Controls.Markdown("# Pricing Overview\n\n*Loading...*")));
    }

    private static string RenderPricingOverview(Pricing pricing)
    {
        var lines = new List<string>
        {
            $"# {pricing.InsuredName}",
            "",
            "## Pricing Details",
            "",
            $"**Pricing ID:** {pricing.Id}",
            $"**Status:** {pricing.Status ?? "N/A"}",
            "",
            "### Coverage Period",
            $"- **Inception Date:** {pricing.InceptionDate?.ToString("yyyy-MM-dd") ?? "N/A"}",
            $"- **Expiration Date:** {pricing.ExpirationDate?.ToString("yyyy-MM-dd") ?? "N/A"}",
            $"- **Underwriting Year:** {pricing.UnderwritingYear?.ToString() ?? "N/A"}",
            "",
            "### Classification",
            $"- **Line of Business:** {pricing.LineOfBusiness ?? "N/A"}",
            $"- **Country:** {pricing.Country ?? "N/A"}",
            "",
            "### Parties",
            $"- **Broker:** {pricing.BrokerName ?? "N/A"}",
            $"- **Primary Insurance:** {pricing.PrimaryInsurance ?? "N/A"}"
        };

        return string.Join("\n", lines);
    }
}
