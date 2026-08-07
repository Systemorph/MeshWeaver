using System;
using System.Threading.Tasks;
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
    [InlineData("/favicon.ico")]
    // 🚨 /static is excluded again (issue #587). It serves BUILD ASSETS ONLY now — read straight
    // out of a shipped assembly's manifest, with no hub post and no permission evaluation. The
    // contract is that /static performs no access check, so it must not resolve a caller to check.
    [InlineData("/static/NodeTypeIcons/box.svg")]
    [InlineData("/static/DocContent/logo.svg")]
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
    // 🚨 The ACCESS-CONTROLLED content route MUST resolve a caller: it posts a GetDataRequest into
    // the mesh (which the never-null PostPipeline guard 500s without an AccessContext), and that
    // request's [RequiresPermission(Read)] is what gates the file. This is where the media that used
    // to be served unauthenticated under /static now lives (issue #587).
    [InlineData("/api/content/AgenticEngineering/content/videos/module1-intro.mp4")]
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
