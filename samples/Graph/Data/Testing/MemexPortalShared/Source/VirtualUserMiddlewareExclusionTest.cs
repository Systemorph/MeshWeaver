// <meshweaver>
// Id: Testing/MemexPortalShared/VirtualUserMiddlewareExclusionTest
// DisplayName: Testing/MemexPortalShared/VirtualUserMiddlewareExclusionTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

public class VirtualUserMiddlewareExclusionTest
{
    [MeshTheory]
    [MeshInlineData("/_framework/blazor.web.js")]
    [MeshInlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [MeshInlineData("/_blazor/negotiate")]
    [MeshInlineData("/static/images/logo.png")]
    [MeshInlineData("/favicon.ico")]
    [MeshInlineData("/mcp")]
    // The anonymous build-identity endpoint: polled by machines that keep no cookie, so the VUser
    // flow could only churn — and it must stay answerable when the mesh is unhealthy.
    [MeshInlineData("/api/version")]
    public async Task ExcludedPrefixes_SkipVirtualUserAssignment(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be excluded and pass through to next");
    }

    [MeshTheory]
    [MeshInlineData("/ACME/Overview")]
    [MeshInlineData("/User/Alice")]
    [MeshInlineData("/")]
    // /api/version is excluded EXACTLY, so a route that merely starts with the same text is not.
    // A prefix match here would silently exempt future routes nobody meant to exempt.
    [MeshInlineData("/api/versioning")]
    [MeshInlineData("/api/version/history")]
    public async Task NonExcludedPaths_AttemptVirtualUserAssignment(string path)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        // Non-excluded paths will attempt to resolve PortalApplication from DI.
        // Since RequestServices isn't set up, this throws.
        var act = () => middleware.InvokeAsync(context);
        await act.Should().ThrowAsync<Exception>(
            because: "non-excluded paths should attempt virtual user assignment via PortalApplication");
    }

    [MeshTheory]
    [MeshInlineData("/_FRAMEWORK/blazor.web.js")]
    [MeshInlineData("/STATIC/image.png")]
    [MeshInlineData("/FAVICON.ICO")]
    public async Task ExcludedPrefixes_AreCaseInsensitive(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: "exclusion should be case-insensitive");
    }

    [MeshTheory]
    [MeshInlineData("/mcp")]
    [MeshInlineData("/mcp/tools")]
    [MeshInlineData("/api/mcp")]
    [MeshInlineData("/api/mcp/tools")]
    public async Task McpPath_StillExcluded(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new VirtualUserMiddleware(next, NullLogger<VirtualUserMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: "MCP paths should remain excluded after refactor");
    }
}
