using System.Reactive.Linq;
using MeshWeaver.Messaging;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Domain;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class StandardPageLayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
    {
        private const string StaticView = nameof(StaticView);


        protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        {
            return base.ConfigureHost(configuration)
                .WithRoutes(r =>
                    r.RouteAddress<ClientAddress>((_, d) => d.Package(r.Hub.JsonSerializerOptions))
                )
                .AddLayout(layout =>
                    layout
                        .WithView(
                            StaticView,
                            Controls.Stack.WithView("Hello", "Hello").WithView("World", "World")
                        )
                        .WithNavMenu((menu, _, _) => menu.WithNavLink("item1", "/item1", "icon1"))
                        .WithNavMenu((menu, _, _) => menu.WithNavLink("item2", "/item2", "icon2"))
                );
        }


        protected override MessageHubConfiguration ConfigureClient(
            MessageHubConfiguration configuration
        ) => base.ConfigureClient(configuration).AddLayoutClient(d => d);

        [HubFact]
        public async Task BasicArea()
        {
            var reference = new LayoutAreaReference(StandardPageLayout.NavMenu);

            var workspace = GetClient().GetWorkspace();
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                new HostAddress(),
                reference
            );

            var control = await stream.GetControlStream(reference.Area)
                .Timeout(10.Seconds())
                .FirstAsync();
            
            control
                .Should()
                .BeOfType<NavMenuControl>()
                .Which.Areas.Should()
                .HaveCount(2)
                ;

        }
    }
}
