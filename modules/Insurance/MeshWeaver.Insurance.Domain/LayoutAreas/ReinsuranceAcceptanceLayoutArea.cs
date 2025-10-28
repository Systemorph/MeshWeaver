using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying reinsurance acceptances associated with a pricing.
/// </summary>
public static class ReinsuranceAcceptanceLayoutArea
{
    // Color definitions for diagram elements
    private static readonly string PricingColor = "#2c7bb6"; // Blue - dark background, white text
    private static readonly string PricingTextColor = "#ffffff";
    private static readonly string AcceptanceColor = "#fdae61"; // Light Orange - light background, dark text
    private static readonly string AcceptanceTextColor = "#000000";
    private static readonly string SectionColor = "#abd9e9"; // Light Blue - light background, dark text
    private static readonly string SectionTextColor = "#000000";

    /// <summary>
    /// Renders the reinsurance acceptances structure for a specific pricing.
    /// </summary>
    public static IObservable<UiControl> ReinsuranceAcceptances(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;
        var acceptanceStream = host.Workspace.GetStream<ReinsuranceAcceptance>()!;
        var sectionStream = host.Workspace.GetStream<ReinsuranceSection>()!;

        return Observable.CombineLatest(
            acceptanceStream,
            sectionStream,
            (acceptances, sections) => (acceptances, sections))
            .Select(data =>
            {
                var acceptanceList = data.acceptances?.ToList() ?? new List<ReinsuranceAcceptance>();
                var sectionList = data.sections?.ToList() ?? new List<ReinsuranceSection>();

                if (!acceptanceList.Any() && !sectionList.Any())
                {
                    return Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ReinsuranceAcceptances"))
                        .WithView(Controls.Markdown("# Reinsurance Structure\n\n*No reinsurance acceptances loaded. Import or add acceptances to begin.*"));
                }

                var diagram = BuildMermaidDiagram(pricingId, acceptanceList, sectionList);
                var mermaidControl = new MarkdownControl($"```mermaid\n{diagram}\n```")
                    .WithStyle(style => style.WithWidth("100%").WithHeight("600px"));

                return Controls.Stack
                    .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ReinsuranceAcceptances"))
                    .WithView(Controls.Title("Reinsurance Structure", 1))
                    .WithView(mermaidControl);
            })
            .StartWith(Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ReinsuranceAcceptances"))
                .WithView(Controls.Markdown("# Reinsurance Structure\n\n*Loading...*")));
    }

    private static string BuildMermaidDiagram(string pricingId, List<ReinsuranceAcceptance> acceptances, List<ReinsuranceSection> sections)
    {
        var sb = new StringBuilder();

        // Start with flowchart definition for cards
        sb.AppendLine("flowchart TD");
        sb.AppendLine("    classDef leftAlign text-align:left");

        // Add the pricing node (main node)
        sb.AppendLine($"    pricing[\"<b>Pricing: {pricingId}</b>\"]");
        sb.AppendLine($"    style pricing fill:{PricingColor},color:{PricingTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class pricing leftAlign");

        // Group sections by acceptanceId
        var sectionsByAcceptance = sections
            .Where(s => s.AcceptanceId != null)
            .GroupBy(s => s.AcceptanceId)
            .ToDictionary(g => g.Key!, g => g.ToList());

        // Add acceptances and their sections
        // Sort acceptances by name to ensure Layer 1, Layer 2, Layer 3 order
        foreach (var acceptance in acceptances.OrderBy(a => a.Name ?? a.Id))
        {
            RenderAcceptance(sb, acceptance, pricingId);

            // Add sections for this acceptance
            // Sort sections by Type first, then by Attach to group coverage types together
            if (sectionsByAcceptance.TryGetValue(acceptance.Id, out var acceptanceSections))
            {
                foreach (var section in acceptanceSections.OrderBy(s => s.Type).ThenBy(s => s.Attach))
                {
                    RenderSection(sb, section, acceptance.Id);
                }
            }
        }

        return sb.ToString();
    }

    private static void RenderAcceptance(StringBuilder sb, ReinsuranceAcceptance acceptance, string pricingId)
    {
        string acceptanceId = SanitizeId(acceptance.Id);
        var acceptanceName = acceptance.Name ?? acceptance.Id;

        // Build acceptance content
        var acceptanceContent = new StringBuilder();
        acceptanceContent.Append($"<b>{acceptanceName}</b>");

        if (acceptance.EPI > 0)
            acceptanceContent.Append($"<br/>EPI: {acceptance.EPI:N0}");

        if (acceptance.Rate > 0)
            acceptanceContent.Append($"<br/>Rate: {acceptance.Rate:P2}");

        if (acceptance.Share > 0)
            acceptanceContent.Append($"<br/>Share: {acceptance.Share:P2}");

        if (acceptance.Cession > 0)
            acceptanceContent.Append($"<br/>Cession: {acceptance.Cession:P2}");

        if (acceptance.Brokerage > 0)
            acceptanceContent.Append($"<br/>Brokerage: {acceptance.Brokerage:P2}");

        if (acceptance.Commission > 0)
            acceptanceContent.Append($"<br/>Commission: {acceptance.Commission:P2}");

        sb.AppendLine($"    acc_{acceptanceId}[\"{acceptanceContent}\"]");
        sb.AppendLine($"    style acc_{acceptanceId} fill:{AcceptanceColor},color:{AcceptanceTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class acc_{acceptanceId} leftAlign");
        sb.AppendLine($"    pricing --> acc_{acceptanceId}");
    }

    private static void RenderSection(StringBuilder sb, ReinsuranceSection section, string acceptanceId)
    {
        string sectionId = SanitizeId(section.Id);
        string sanitizedAcceptanceId = SanitizeId(acceptanceId);

        // Build section content
        var sectionContent = new StringBuilder();
        sectionContent.Append($"<b>{section.Name ?? section.Type ?? section.Id}</b><br/>");

        if (!string.IsNullOrEmpty(section.Type) && section.Type != section.Name)
        {
            sectionContent.Append($"Type: {section.Type}<br/>");
        }

        sectionContent.Append($"Attach: {section.Attach:N0}<br/>");
        sectionContent.Append($"Limit: {section.Limit:N0}<br/>");

        if (section.AggAttach.HasValue && section.AggAttach.Value > 0)
        {
            sectionContent.Append($"AAD: {section.AggAttach.Value:N0}<br/>");
        }

        if (section.AggLimit.HasValue && section.AggLimit.Value > 0)
        {
            sectionContent.Append($"AAL: {section.AggLimit.Value:N0}");
        }

        // Create node
        sb.AppendLine($"    sec_{sectionId}[\"{sectionContent}\"]");
        sb.AppendLine($"    style sec_{sectionId} fill:{SectionColor},color:{SectionTextColor},stroke:#333,stroke-width:1px");
        sb.AppendLine($"    class sec_{sectionId} leftAlign");
        sb.AppendLine($"    acc_{sanitizedAcceptanceId} --> sec_{sectionId}");
    }

    private static string SanitizeId(string id)
    {
        // Replace characters that might cause issues in Mermaid IDs
        return id.Replace("-", "_").Replace(" ", "_").Replace(".", "_");
    }
}
