﻿using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith.Test
{
    public static class TestHubExtensions
    {
        public static IMessageHub CreateTestHub(IMessageHub mesh)
            => mesh.ServiceProvider.CreateMessageHub(new ApplicationAddress(nameof(Test)), config => config
                .AddLayout(layout =>
                    layout.WithView(ctx =>
                            ctx.Area == "Dashboard",
                        (_, ctx) => new LayoutGridControl()
                    )
                )
            );
        public static readonly MeshNode Node = new(ApplicationAddress.TypeName, nameof(Test), nameof(Test), typeof(TestHubExtensions).Assembly.FullName)
        {
            StartupScript = $"""
                             #r "{typeof(TestHubExtensions).Namespace}"
                             {typeof(TestHubExtensions).FullName}.{nameof(CreateTestHub)}(Mesh);
                             """
        };
        public static readonly string GetDashboardCommand =
            @"
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
await Mesh.GetLayoutAreaAsync(new ApplicationAddress(""Test""), ""Dashboard"")";

    }
}