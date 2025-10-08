using System.Reactive.Linq;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying submission details for a pricing.
/// </summary>
public static class SubmissionLayoutArea
{
    /// <summary>
    /// Renders the submission details for a specific pricing.
    /// </summary>
    public static IObservable<UiControl> Submission(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;

        return host.Workspace.GetStream<Pricing>()!
            .Select(pricings =>
            {
                var pricing = pricings?.FirstOrDefault();
                return pricing != null
                    ? Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                        .WithView(Controls.Markdown(RenderSubmissionDetails(pricing)))
                    : Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                        .WithView(Controls.Markdown($"# Submission\n\n*Pricing '{pricingId}' not found.*"));
            })
            .StartWith(Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                .WithView(Controls.Markdown("# Submission\n\n*Loading...*")));
    }

    private static string RenderSubmissionDetails(Pricing pricing)
    {
        var lines = new List<string>
        {
            $"# Submission - {pricing.InsuredName}",
            "",
            "## Submission Information",
            "",
            $"**Pricing ID:** {pricing.Id}",
            $"**Status:** {pricing.Status ?? "N/A"}",
            $"**Broker:** {pricing.BrokerName ?? "N/A"}",
            "",
            "### Coverage Period",
            $"- **Inception Date:** {pricing.InceptionDate?.ToString("yyyy-MM-dd") ?? "N/A"}",
            $"- **Expiration Date:** {pricing.ExpirationDate?.ToString("yyyy-MM-dd") ?? "N/A"}",
            $"- **Underwriting Year:** {pricing.UnderwritingYear?.ToString() ?? "N/A"}",
            "",
            "### Classification",
            $"- **Line of Business:** {pricing.LineOfBusiness ?? "N/A"}",
            $"- **Country:** {pricing.Country ?? "N/A"}",
            $"- **Legal Entity:** {pricing.LegalEntity ?? "N/A"}",
            "",
            "---",
            "",
            "*Additional submission details coming soon...*"
        };

        return string.Join("\n", lines);
    }
}
