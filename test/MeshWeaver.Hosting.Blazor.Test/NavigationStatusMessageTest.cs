using FluentAssertions;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pure unit tests for the <see cref="NavigationStatus"/> record's factory
/// methods. These pin the "no endless spinner" contract at the data layer:
/// every factory must return a non-empty, non-whitespace <c>Message</c>, and
/// the address/area composition must match the UX spec.
/// </summary>
public class NavigationStatusMessageTest
{
    [Fact]
    public void Idle_HasNonEmptyMessage()
    {
        var s = NavigationStatus.Idle();
        s.Phase.Should().Be(NavigationPhase.Idle);
        s.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("FutuRe/EuropeRe")]
    [InlineData("ACME")]
    public void LookingUp_WithPath_IncludesPathInMessage(string path)
    {
        var s = NavigationStatus.LookingUp(path);
        s.Phase.Should().Be(NavigationPhase.LookingUp);
        s.Message.Should().Contain(path);
        s.Message.Should().Contain("Looking up", "the user expects to see that we're looking up the page");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LookingUp_WithoutPath_StillHasNonEmptyMessage(string? path)
    {
        var s = NavigationStatus.LookingUp(path);
        s.Message.Should().NotBeNullOrWhiteSpace("never an empty spinner");
        s.Message.Should().Contain("Looking up");
    }

    [Fact]
    public void Redirecting_NullArea_DoesNotMentionArea()
    {
        var s = NavigationStatus.Redirecting("ACME/Project", null);
        s.Phase.Should().Be(NavigationPhase.Redirecting);
        s.Message.Should().Contain("Redirecting to ACME/Project");
        s.Message.Should().NotContain("area", "no area means no area in the message");
    }

    [Fact]
    public void Redirecting_EmptyArea_DoesNotMentionArea()
    {
        var s = NavigationStatus.Redirecting("ACME/Project", "");
        s.Message.Should().Contain("Redirecting to ACME/Project");
        s.Message.Should().NotContain("area");
    }

    [Fact]
    public void Redirecting_WithArea_IncludesArea()
    {
        var s = NavigationStatus.Redirecting("ACME/Project", "Overview");
        s.Message.Should().Contain("ACME/Project");
        s.Message.Should().Contain("area Overview");
    }

    [Fact]
    public void Loading_IncludesAddress()
    {
        var s = NavigationStatus.Loading("ACME/Project");
        s.Phase.Should().Be(NavigationPhase.Loading);
        s.Message.Should().Contain("ACME/Project");
        s.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Compiling_IncludesNodeTypePath()
    {
        var s = NavigationStatus.Compiling("ACME/Project/Story", 3);
        s.Phase.Should().Be(NavigationPhase.Loading);
        s.Message.Should().Contain("Compiling");
        s.Message.Should().Contain("ACME/Project/Story");
        s.Message.Should().Contain("3");
    }

    [Fact]
    public void Compiling_ZeroSeconds_OmitsSecondsSuffix()
    {
        var s = NavigationStatus.Compiling("ACME/Project/Story", 0);
        s.Message.Should().Contain("Compiling node type ACME/Project/Story");
        s.Message.Should().NotContain("(0");
    }

    [Fact]
    public void Subscribing_WithArea_MentionsBoth()
    {
        var s = NavigationStatus.Subscribing("ACME/Project", "Dashboard");
        s.Phase.Should().Be(NavigationPhase.Loading);
        s.Message.Should().Contain("Subscribing");
        s.Message.Should().Contain("Dashboard");
        s.Message.Should().Contain("ACME/Project");
    }

    [Fact]
    public void Subscribing_WithoutArea_StillHasMessage()
    {
        var s = NavigationStatus.Subscribing("ACME/Project", null);
        s.Message.Should().Contain("Subscribing");
        s.Message.Should().Contain("ACME/Project");
        s.Message.Should().NotContain("area");
    }

    [Fact]
    public void Ready_IncludesAddress()
    {
        var s = NavigationStatus.Ready("ACME/Project");
        s.Phase.Should().Be(NavigationPhase.Ready);
        s.Message.Should().Contain("ACME/Project");
    }

    [Fact]
    public void NotFound_WithPath_IncludesPath()
    {
        var s = NavigationStatus.NotFound("does/not/exist");
        s.Phase.Should().Be(NavigationPhase.NotFound);
        s.Message.Should().Contain("does/not/exist");
        s.Message.Should().Contain("not found", "exact wording users search for");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NotFound_WithoutPath_StillHasMessage(string? path)
    {
        var s = NavigationStatus.NotFound(path);
        s.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Error_IncludesErrorText()
    {
        var s = NavigationStatus.Error("boom");
        s.Phase.Should().Be(NavigationPhase.Error);
        s.Message.Should().Contain("boom");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Error_WithoutText_StillHasMessage(string? msg)
    {
        var s = NavigationStatus.Error(msg!);
        s.Message.Should().NotBeNullOrWhiteSpace();
    }
}
