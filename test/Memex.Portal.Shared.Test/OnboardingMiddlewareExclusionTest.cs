using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

public class OnboardingMiddlewareExclusionTest
{
    private static ClaimsPrincipal AuthenticatedUser =>
        new(new ClaimsIdentity("TestAuth"));

    [Theory]
    [InlineData("/onboarding")]
    [InlineData("/login")]
    [InlineData("/auth/callback")]
    [InlineData("/_framework/blazor.js")]
    [InlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [InlineData("/static/img.png")]
    [InlineData("/favicon.ico")]
    [InlineData("/mcp")]
    [InlineData("/signin-microsoft")]
    public async Task ExcludedPrefixes_SkipOnboardingCheck(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be excluded and pass through to next");
    }

    [Theory]
    [InlineData("/signin-microsoft")]
    [InlineData("/signin-google")]
    [InlineData("/signin-oidc")]
    public async Task SigninCallbackPaths_AreExcluded(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be excluded by the /signin- prefix");
    }

    [Theory]
    [InlineData("/ACME/Overview")]
    [InlineData("/User/Alice")]
    [InlineData("/")]
    public async Task NonExcludedPaths_AttemptOnboardingCheck(string path)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        // Non-excluded paths with authenticated user will attempt to resolve
        // PortalApplication from DI. Since RequestServices isn't set up, this throws.
        var act = () => middleware.InvokeAsync(context);
        await act.Should().ThrowAsync<Exception>(
            because: "non-excluded paths should attempt onboarding check via PortalApplication");
    }

    [Theory]
    [InlineData("/_FRAMEWORK/blazor.js")]
    [InlineData("/LOGIN")]
    [InlineData("/STATIC/img.png")]
    public async Task ExcludedPrefixes_AreCaseInsensitive(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: "exclusion should be case-insensitive");
    }

    [Theory]
    [InlineData("/ACME/Overview")]
    [InlineData("/User/Alice")]
    [InlineData("/")]
    public async Task UnauthenticatedUser_SkipsEntireCheck(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        // No user set — unauthenticated

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(
            because: "unauthenticated users should skip the entire onboarding check");
    }
}
