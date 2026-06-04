using System.Net.Http;
using Memex.Portal.Shared.Teams;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the Teams bot's inbound security gate — the part that must hold on CI where there is no Bot
/// credential and no network. The messaging endpoint is anonymous at the pipeline, so
/// <see cref="ITeamsClient.ValidateInboundAsync"/> is the only thing standing between a forged POST and
/// agent work: it must reject anything that isn't a Bot Framework bearer token, and the whole channel must
/// be inert when not configured. All cases here return before any network call (no Bot Framework metadata
/// fetch), so they are deterministic on CI.
/// </summary>
public class TeamsClientValidationTests
{
    private static TeamsClient Make(bool enabled) =>
        new(new TeamsOptions
        {
            Enabled = enabled,
            AppId = enabled ? "00000000-0000-0000-0000-000000000001" : null,
            AppPassword = enabled ? "secret" : null
        }, new HttpClient());

    [Fact]
    public void Disabled_IsNotConfigured() => Assert.False(Make(false).IsConfigured);

    [Fact]
    public void EnabledWithCredentials_IsConfigured() => Assert.True(Make(true).IsConfigured);

    [Theory]
    [InlineData(null)]            // no header
    [InlineData("")]              // empty header
    [InlineData("garbage")]       // not a Bearer scheme
    [InlineData("Basic abc123")]  // wrong scheme
    public async Task ValidateInbound_RejectsNonBearerOrMissing_NoNetwork(string? header)
    {
        // Configured bot, but a non-Bearer/missing header is rejected before any metadata fetch.
        Assert.False(await Make(true).ValidateInboundAsync(header, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateInbound_WhenDisabled_AlwaysFalse()
    {
        // Inert channel: even a Bearer-shaped header is rejected outright (no network).
        Assert.False(await Make(false).ValidateInboundAsync("Bearer anything", TestContext.Current.CancellationToken));
    }
}
