using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates top clients analysis with dynamic tables and reward suggestions.
/// Provides comprehensive client analysis with purchase rankings and personalized reward strategies.
/// </summary>
public static class TopClientsArea
{
    /// <summary>
    /// Adds top clients analysis views to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the top clients views will be added.</param>
    /// <returns>The updated layout definition with top clients views.</returns>
    public static LayoutDefinition AddTopClients(this LayoutDefinition layout)
        => layout.WithView(nameof(TopClientsTable), TopClientsTable)
            .WithView(nameof(TopClientsRewardSuggestions), TopClientsRewardSuggestions);

    /// <summary>
    /// Displays a markdown table showing the top 5 clients by purchase amount.
    /// Calculates total purchase amounts and ranks clients by spending.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown table with top clients ranking.</returns>
    public static IObservable<UiControl> TopClientsTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .Select(tuple =>
            {
                var data = tuple.First.ToList();
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c);

                var topClients = data
                    .GroupBy(od => od.Customer)
                    .Select(g => new
                    {
                        CustomerName = customers.TryGetValue(g.Key ?? "", out var customer) ? customer.CompanyName : "Unknown",
                        TotalAmount = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(c => c.TotalAmount)
                    .Take(5)
                    .ToList();

                var markdown = new StringBuilder();
                markdown.AppendLine("### Top 5 Clients by Purchase Amount");
                markdown.AppendLine();
                markdown.AppendLine("| Rank | Client Name | Total Purchase Amount |");
                markdown.AppendLine("|------|-------------|----------------------|");

                for (int i = 0; i < topClients.Count; i++)
                {
                    var client = topClients[i];
                    markdown.AppendLine($"| {i + 1} | **{client.CustomerName}** | ${client.TotalAmount:N2} |");
                }

                markdown.AppendLine();
                markdown.AppendLine("*Analysis based on total purchase amounts across all orders*");

                return (UiControl)Controls.Markdown(markdown.ToString());
            });

    /// <summary>
    /// Displays personalized reward suggestions for top clients.
    /// Generates customized reward strategies based on client ranking and spending.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown report with personalized reward suggestions.</returns>
    public static IObservable<UiControl> TopClientsRewardSuggestions(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .Select(tuple =>
            {
                var data = tuple.First.ToList();
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c);

                var topClients = data
                    .GroupBy(od => od.Customer)
                    .Select(g => new
                    {
                        CustomerName = customers.TryGetValue(g.Key ?? "", out var customer) ? customer.CompanyName : "Unknown",
                        TotalAmount = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(c => c.TotalAmount)
                    .Take(5)
                    .ToList();

                var markdown = new StringBuilder();
                markdown.AppendLine("### Personalized Reward Suggestions");
                markdown.AppendLine();

                var rewardStrategies = new[]
                {
                    new { Strategy = "10% discount on next purchase", Icon = "💰" },
                    new { Strategy = "Exclusive early access to new products", Icon = "🎯" },
                    new { Strategy = "VIP client appreciation event invitation", Icon = "🎉" },
                    new { Strategy = "Dedicated account manager service", Icon = "👤" },
                    new { Strategy = "Premium thank-you gift basket", Icon = "🎁" },
                    new { Strategy = "Free shipping for 6 months", Icon = "🚚" },
                    new { Strategy = "Client spotlight feature", Icon = "⭐" },
                    new { Strategy = "Loyalty points program", Icon = "🏆" },
                    new { Strategy = "Personalized CEO thank-you note", Icon = "📝" },
                    new { Strategy = "Complimentary product samples", Icon = "🎈" }
                };

                for (int i = 0; i < topClients.Count; i++)
                {
                    var client = topClients[i];
                    var rewards = rewardStrategies.Skip(i * 2).Take(3);

                    markdown.AppendLine($"#### {i + 1}. **{client.CustomerName}** (${client.TotalAmount:N2})");
                    foreach (var reward in rewards)
                    {
                        markdown.AppendLine($"- {reward.Icon} **{reward.Strategy}**");
                    }
                    markdown.AppendLine();
                }

                return (UiControl)Controls.Markdown(markdown.ToString());
            });
}