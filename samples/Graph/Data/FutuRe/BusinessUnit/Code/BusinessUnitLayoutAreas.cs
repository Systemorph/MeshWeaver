// <meshweaver>
// Id: BusinessUnitLayoutAreas
// DisplayName: Business Unit Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Views for displaying business unit details.
/// </summary>
public static class BusinessUnitLayoutAreas
{
    private const string BuDataId = "businessUnit";

    public static LayoutDefinition AddBusinessUnitLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(Overview), Overview);

    /// <summary>
    /// Business unit overview showing name, region, currency, and description.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> Overview(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(BuDataId, host.Workspace.GetNodeContent<BusinessUnit>().Where(x => x != null));

        return host.GetDataStream<BusinessUnit>(BuDataId)
            .Select(bu =>
            {
                if (bu == null)
                    return null;

                var stack = Controls.Stack
                    .WithView(Controls.H2(bu.Name));

                if (!string.IsNullOrEmpty(bu.Region))
                    stack = stack.WithView(Controls.Markdown($"**Region:** {bu.Region}"));

                stack = stack.WithView(Controls.Markdown($"**Reporting Currency:** {bu.Currency}"));

                if (!string.IsNullOrEmpty(bu.Description))
                    stack = stack.WithView(Controls.Markdown(bu.Description));

                stack = stack.WithView(LayoutAreaControl.Children(host.Hub));

                return (UiControl?)stack;
            });
    }
}
