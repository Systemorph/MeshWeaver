using System;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

public class VirtualUserMiddlewareExclusionTest
{
    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/static/images/logo.png")]
    [InlineData("/favicon.ico")]
    [InlineData("/mcp")]
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

    [Theory]
    [InlineData("/ACME/Overview")]
    [InlineData("/User/Alice")]
    [InlineData("/")]
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

    [Theory]
    [InlineData("/_FRAMEWORK/blazor.web.js")]
    [InlineData("/STATIC/image.png")]
    [InlineData("/FAVICON.ICO")]
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

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/tools")]
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
