using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

public static class ImportConfigsLayoutArea
{
    public static IObservable<UiControl> ImportConfigs(LayoutAreaHost host, RenderingContext ctx)
    {
        _ = ctx;
        var pricingId = host.Hub.Address.Id;
        var pricingStream = host.Workspace.GetStream<Pricing>()!;
        var cfgStream = host.Workspace.GetStream<ExcelImportConfiguration>()!;

        return cfgStream.CombineLatest(pricingStream, (cfgs, pricings) =>
        {
            var pricing = pricings?.FirstOrDefault();

            var list = cfgs?
                .Where(c => string.Equals(c.EntityId, pricingId, StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<ExcelImportConfiguration>();

            if (list.Count == 0)
            {
                return Controls.Stack
                    .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ImportConfigs"))
                    .WithView(Controls.Markdown("# Import Configurations\n\n*No import configurations found for this pricing.*"));
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var parts = new List<string> { "# Import Configurations" };
            parts.Add($"\n**Pricing:** {pricingId}\n");

            foreach (var cfg in list.OrderBy(x => x.Name))
            {
                parts.Add($"\n## {cfg.Name}");
                parts.Add($"\n**Worksheet:** {cfg.WorksheetName}");
                parts.Add($"**Data Start Row:** {cfg.DataStartRow}");

                if (cfg.Mappings.Any())
                {
                    parts.Add("\n### Column Mappings");
                    parts.Add("\n| Target Property | Mapping Kind | Source Columns | Constant Value |");
                    parts.Add("|----------------|--------------|----------------|----------------|");
                    foreach (var mapping in cfg.Mappings)
                    {
                        var sourceColumns = string.Join(", ", mapping.SourceColumns);
                        var constantValue = mapping.ConstantValue?.ToString() ?? "";
                        parts.Add($"| {mapping.TargetProperty} | {mapping.Kind} | {sourceColumns} | {constantValue} |");
                    }
                }

                if (cfg.Allocations.Any())
                {
                    parts.Add("\n### Allocations");
                    parts.Add("\n| Target Property | Total Cell | Weight Columns | Currency Property |");
                    parts.Add("|----------------|------------|----------------|-------------------|");
                    foreach (var alloc in cfg.Allocations)
                    {
                        var weightColumns = string.Join(", ", alloc.WeightColumns);
                        parts.Add($"| {alloc.TargetProperty} | {alloc.TotalCell} | {weightColumns} | {alloc.CurrencyProperty ?? ""} |");
                    }
                }

                if (cfg.TotalRowMarkers.Any())
                {
                    parts.Add($"\n**Total Row Markers:** {string.Join(", ", cfg.TotalRowMarkers)}");
                }

                if (cfg.IgnoreRowExpressions.Any())
                {
                    parts.Add("\n**Ignore Row Expressions:**");
                    foreach (var expr in cfg.IgnoreRowExpressions)
                    {
                        parts.Add($"- `{expr}`");
                    }
                }

                // Add full JSON configuration in a collapsible section
                var json = JsonSerializer.Serialize(cfg, options);
                parts.Add($"\n<details>\n<summary>View Full JSON Configuration</summary>\n\n```json\n{json}\n```\n</details>\n");
            }

            var md = string.Join("\n", parts);

            return Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ImportConfigs"))
                .WithView(Controls.Markdown(md));
        })
        .StartWith(Controls.Stack
            .WithView(PricingLayoutShared.BuildToolbar(pricingId, "ImportConfigs"))
            .WithView(Controls.Markdown("# Import Configurations\n\n*Loading...*")));
    }
}
