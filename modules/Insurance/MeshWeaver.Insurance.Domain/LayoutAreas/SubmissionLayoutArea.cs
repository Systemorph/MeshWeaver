using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Insurance.Domain.LayoutAreas.Shared;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using static MeshWeaver.ContentCollections.ContentCollectionsExtensions;

namespace MeshWeaver.Insurance.Domain.LayoutAreas;

/// <summary>
/// Layout area for displaying submission details for a pricing.
/// Uses the generic Collection layout area with area:'Collection', Id:'{collection}/{path}'
/// </summary>
public static class SubmissionLayoutArea
{
    /// <summary>
    /// Renders the submission details for a specific pricing using the generic Collection layout area.
    /// </summary>
    public static IObservable<UiControl> Submission(LayoutAreaHost host, RenderingContext _)
    {
        var pricingId = host.Hub.Address.Id;

        return host.Workspace.GetStream<Pricing>()!
            .Select(pricings =>
            {
                var pricing = pricings?.FirstOrDefault();
                if (pricing != null)
                {
                    // Get the collection configuration, creating a localized version if needed
                    var localizedCollectionName = GetLocalizedCollectionName("Submissions", pricingId);
                    var contentService = host.Hub.ServiceProvider.GetRequiredService<IContentService>();

                    // Get the collection configuration
                    var collectionConfig = contentService.GetCollectionConfig(localizedCollectionName);

                    var fileBrowser = new FileBrowserControl(localizedCollectionName);
                    if (collectionConfig != null)
                        fileBrowser = fileBrowser
                            .WithCollectionConfiguration(collectionConfig)
                            .CreatePath();

                    return Controls.Stack
                        .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                        .WithView(Controls.Title($"Submission - {pricing.InsuredName}", 1))
                        .WithView(fileBrowser);
                }
                return Controls.Stack
                    .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                    .WithView(Controls.Markdown($"# Submission\n\n*Pricing '{pricingId}' not found.*"));
            })
            .StartWith(Controls.Stack
                .WithView(PricingLayoutShared.BuildToolbar(pricingId, "Submission"))
                .WithView(Controls.Markdown("# Submission\n\n*Loading...*")));
    }
}
