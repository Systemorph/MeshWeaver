using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MeshWeaver.Blazor.Infrastructure;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

public class UserContextMiddlewareExclusionTest
{
    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/static/images/logo.png")]
    [InlineData("/favicon.ico")]
    public async Task ExcludedPrefixes_SkipUserResolution(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var logger = NullLogger<UserContextMiddleware>.Instance;
        var middleware = new UserContextMiddleware(next, logger);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        // Should call next() without trying to resolve PortalApplication
        // (which isn't registered, so it would throw if it tried)
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be skipped and pass through to next");
    }

    [Theory]
    [InlineData("/ACME/Overview")]
    [InlineData("/User/Alice")]
    [InlineData("/")]
    public async Task NonExcludedPaths_AttemptUserResolution(string path)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var logger = NullLogger<UserContextMiddleware>.Instance;
        var middleware = new UserContextMiddleware(next, logger);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        // Non-excluded paths will attempt to resolve PortalApplication from DI.
        // Since RequestServices isn't set up, this throws (proving the path was NOT skipped).
        var act = () => middleware.InvokeAsync(context);
        await act.Should().ThrowAsync<Exception>(
            because: "non-excluded paths should attempt PortalApplication resolution");
    }

    [Theory]
    [InlineData("/_FRAMEWORK/blazor.web.js")]
    [InlineData("/_Content/something")]
    [InlineData("/Static/image.png")]
    [InlineData("/FAVICON.ICO")]
    public async Task ExcludedPrefixes_AreCaseInsensitive(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var logger = NullLogger<UserContextMiddleware>.Instance;
        var middleware = new UserContextMiddleware(next, logger);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: "exclusion should be case-insensitive");
    }
}
