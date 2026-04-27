using FluentAssertions;
using MeshWeaver.Blazor.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

public class NonfileRouteConstraintTest
{
    private readonly NonfileRouteConstraint _constraint = new();

    private bool Match(string path)
    {
        var values = new RouteValueDictionary { ["Path"] = path };
        return _constraint.Match(null, null, "Path", values, RouteDirection.IncomingRequest);
    }

    [Theory]
    [InlineData("signin-microsoft")]
    [InlineData("signin-google")]
    [InlineData("signin-linkedin")]
    [InlineData("signin-apple")]
    [InlineData("signin-microsoft/callback")]
    [InlineData("signin-google/callback")]
    public void OAuthCallbackPaths_AreExcluded(string path)
    {
        Match(path).Should().BeFalse(because: $"'{path}' is an OAuth callback and should be excluded");
    }

    [Theory]
    [InlineData("_framework")]
    [InlineData("_framework/blazor.web.js")]
    [InlineData("_content")]
    [InlineData("_content/MeshWeaver.Blazor/css/app.css")]
    [InlineData("_blazor")]
    [InlineData("favicon.ico")]
    [InlineData("auth")]
    [InlineData("auth/login")]
    [InlineData("dev")]
    [InlineData("dev/login")]
    [InlineData("mcp")]
    [InlineData("mcp/sse")]
    public void StaticAndInfrastructurePaths_AreExcluded(string path)
    {
        Match(path).Should().BeFalse(because: $"'{path}' should be excluded");
    }

    [Theory]
    [InlineData("ACME/Overview")]
    [InlineData("User/Alice")]
    [InlineData("Northwind/Dashboard")]
    [InlineData("Doc/Architecture")]
    [InlineData("Organization/Search")]
    public void NormalApplicationPaths_PassThrough(string path)
    {
        Match(path).Should().BeTrue(because: $"'{path}' is a normal route and should pass through");
    }

    [Fact]
    public void EmptyPath_ReturnsTrue()
    {
        var values = new RouteValueDictionary { ["Path"] = "" };
        _constraint.Match(null, null, "Path", values, RouteDirection.IncomingRequest)
            .Should().BeTrue();
    }

    [Fact]
    public void MissingRouteValue_ReturnsTrue()
    {
        var values = new RouteValueDictionary();
        _constraint.Match(null, null, "Path", values, RouteDirection.IncomingRequest)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("SIGNIN-MICROSOFT")]
    [InlineData("Signin-Google")]
    [InlineData("_FRAMEWORK")]
    [InlineData("_Content")]
    public void ExcludedPaths_AreCaseInsensitive(string path)
    {
        Match(path).Should().BeFalse(because: "exclusion should be case-insensitive");
    }
}
