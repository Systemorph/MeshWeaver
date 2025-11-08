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
        var cfgStream = host.Workspace.GetStream<ExcelImportConfiguration>()!;

        return cfgStream.Select(cfgs =>
        {
            var list = cfgs?
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
                // Add full JSON configuration in a collapsible section
                var json = JsonSerializer.Serialize(cfg, options);
                parts.Add($"```json\n{json}\n```");
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
