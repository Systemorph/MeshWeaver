using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins <see cref="PluginRegistryTokens.Validate"/> — the instance-token gate of the plugin-registry
/// surface: only a <c>Bearer</c> header carrying one of the registry's issued tokens passes; a
/// missing header, a wrong scheme, an unknown token, or an empty issued list all fail closed.
/// </summary>
public class PluginRegistryTokensTest
{
    private static readonly string[] Issued = ["tok-instance-1", "tok-instance-2"];

    [Theory]
    [InlineData("Bearer tok-instance-1")]
    [InlineData("Bearer tok-instance-2")]
    [InlineData("bearer tok-instance-1")] // scheme is case-insensitive
    [InlineData("  Bearer tok-instance-1  ")] // surrounding whitespace is tolerated
    public void IssuedToken_Passes(string header)
    {
        Assert.True(PluginRegistryTokens.Validate(header, Issued));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer")] // scheme without a token
    [InlineData("Bearer tok-unknown")]
    [InlineData("Bearer tok-instance")] // prefix of an issued token
    [InlineData("Bearer tok-instance-11")] // issued token plus a suffix
    [InlineData("Basic tok-instance-1")] // wrong scheme
    [InlineData("tok-instance-1")] // bare token without a scheme
    public void AnythingElse_FailsClosed(string? header)
    {
        Assert.False(PluginRegistryTokens.Validate(header, Issued));
    }

    [Fact]
    public void NoIssuedTokens_NeverValidates()
    {
        // The endpoints only consult Validate when tokens ARE configured (no tokens → the open
        // dev/e2e registry); Validate itself still fails closed on an empty list.
        Assert.False(PluginRegistryTokens.Validate("Bearer tok-instance-1", []));
    }

    [Fact]
    public void AuthorizationHeader_RoundTripsThroughValidate()
    {
        Assert.True(PluginRegistryTokens.Validate(
            PluginRegistryTokens.AuthorizationHeader("tok-instance-1"), Issued));
    }
}
