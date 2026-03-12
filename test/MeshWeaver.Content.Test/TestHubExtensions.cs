using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Content.Test;

public static class TestHubExtensions
{
    public static IMessageHub CreateTestHub(IMessageHub mesh)
        => mesh.ServiceProvider.CreateMessageHub(AddressExtensions.CreateAppAddress(nameof(Content)), config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        ctx.Area == "Dashboard",
                    (_, ctx) => new LayoutGridControl()
                )
            )
        );
    public static readonly MeshNode Node = new(nameof(Content), AddressExtensions.AppType)
    {
        Name = nameof(Content),
        HubConfiguration = config => config.AddLayout(layout =>
            layout.WithView(ctx => ctx.Area == "Dashboard",
                (_, ctx) => new LayoutGridControl()))
    };
    public static readonly string GetDashboardCommand =
        @$"
using static MeshWeaver.Layout.Controls;
using MeshWeaver.Messaging;
LayoutArea(AddressExtensions.CreateAppAddress(""Content""), ""Dashboard"")";

}
