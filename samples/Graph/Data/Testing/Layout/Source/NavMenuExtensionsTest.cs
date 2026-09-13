// <meshweaver>
// Id: Testing/Layout/NavMenuExtensionsTest
// DisplayName: Testing/Layout/NavMenuExtensionsTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
﻿using System.Reactive.Linq;
using MeshWeaver.Messaging;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;

public class NavMenuExtensionsTest(MeshTestContext context) : InMeshTestBase(output)
{
    public class LayoutTest(MeshTestContext context) : InMeshTestBase(output)
    {
        private const string StaticView = nameof(StaticView);


        protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        {
            return base.ConfigureHost(configuration)
                .WithRoutes(r =>
                    r.RouteAddress(ClientType, (_, d) => d.Package())
                )
                .AddLayout(layout =>
                    layout
                        .WithView(
                            StaticView,
                            Controls.Stack.WithView(Controls.Html("Hello"), "Hello").WithView(Controls.Html("World"), "World")
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
            var reference = new LayoutAreaReference(NavMenuExtensions.NavMenu);

            var workspace = GetClient().GetWorkspace();
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                CreateHostAddress(),
                reference
            );

            var control = await stream.GetControlStream(reference.Area!)
                .Should().Within(10.Seconds()).Match(x => x != null);

            control
                .Should()
                .BeOfType<NavMenuControl>()
                .Which.Areas.Should()
                .HaveCount(2)
                ;

        }
    }
}
