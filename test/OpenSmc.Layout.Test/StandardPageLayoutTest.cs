using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using System.Text.Json;
using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Layout.Domain;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

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
                        .WithStandardPageLayout()
                        .WithView(
                            StaticView,
                            Controls.Stack.WithView("Hello", "Hello").WithView("World", "World")
                        )
                        .WithNavMenu((menu, _) => menu.WithNavLink("item1", "/item1", "icon1"))
                        .WithNavMenu((menu, _) => menu.WithNavLink("item2", "/item2", "icon2"))
                );
        }


        protected override MessageHubConfiguration ConfigureClient(
            MessageHubConfiguration configuration
        ) => base.ConfigureClient(configuration).AddLayoutClient(d => d);

        [HubFact]
        public async Task BasicArea()
        {
            var reference = new LayoutAreaReference(StaticView) { Layout = StandardPageLayout.Page };

            var workspace = GetClient().GetWorkspace();
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                new HostAddress(),
                reference
            );

            var control = await stream.GetControl(reference.Area);
            control
                .Should()
                .BeOfType<LayoutStackControl>()
                .Which.Areas.Should()
                .HaveCount(2)
                ;

            var page = await stream.GetControl(StandardPageLayout.Page);
            var stack = page.Should().BeOfType<LayoutStackControl>().Which;
                stack.Areas.Should().HaveCountGreaterThan(0)
                .And.Subject.Should().BeEquivalentTo(Enumerable.Range(1, stack.Areas.Count).Select(i => $"{StandardPageLayout.Page}/{i}"))
                ;

            var header = await stream.GetControl(StandardPageLayout.Header);
            header.Should().BeOfType<LayoutStackControl>()
                .Which.Areas.Should().HaveCountGreaterThan(0);
            var footer = await stream.GetControl(StandardPageLayout.Footer);
            footer.Should().BeOfType<LayoutStackControl>()
                .Which.Areas.Should().HaveCountGreaterThan(0);
            var mainContent = await stream.GetControl(StandardPageLayout.MainContent);
            mainContent.Should().BeOfType<NamedAreaControl>()
                .Which.Data.Should().Be(reference.Area);

            var navMenu = await stream.GetControl(StandardPageLayout.NavMenu);
            navMenu.Should().BeOfType<NavMenuControl>()
                .Which.Areas.Should().HaveCount(2)
                ;
        }
    }
}
