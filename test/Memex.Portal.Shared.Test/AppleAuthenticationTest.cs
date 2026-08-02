using System.Security.Cryptography;
using System.Text;
using AspNet.Security.OAuth.Apple;
using MeshWeaver.Blazor.Portal.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The Sign in with Apple wiring. Apple deviates from generic OAuth in two ways that broke the
/// previous hand-rolled AddOAuth("Apple") registration: requesting name/email scopes requires
/// response_mode=form_post (a cross-site POST callback the generic handler cannot parse), and
/// there is no static client secret — it is an ES256 JWT minted from the .p8 key. These tests pin
/// the configuration wiring into the AspNet.Security.OAuth.Apple handler (which owns the protocol)
/// and the private-key normalization for every shape an environment can deliver the key in.
/// The full authorize → form_post callback → cookie flow is Apple-server-dependent and is
/// exercised manually against the live instance.
/// </summary>
public class AppleAuthenticationTest
{
    private static (string Pem, byte[] Pkcs8) CreateKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        return (PemEncoding.WriteString("PRIVATE KEY", pkcs8), pkcs8);
    }

    private static void AssertImportable(string pem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem); // throws if the normalized form is not valid PEM/PKCS#8
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePrivateKey_NoKeyConfigured_ReturnsNull(string? value) =>
        Assert.Null(AuthenticationBuilderExtensions.NormalizePrivateKey(value));

    [Fact]
    public void NormalizePrivateKey_RawPem_PassesThroughImportable()
    {
        var (pem, _) = CreateKey();
        var normalized = AuthenticationBuilderExtensions.NormalizePrivateKey(pem);
        Assert.NotNull(normalized);
        AssertImportable(normalized);
    }

    [Fact]
    public void NormalizePrivateKey_PemWithLiteralNewlineEscapes_Importable()
    {
        // A single-line env var: the PEM's newlines arrive as the two characters '\' 'n'.
        var (pem, _) = CreateKey();
        var normalized = AuthenticationBuilderExtensions.NormalizePrivateKey(pem.Replace("\n", "\\n"));
        Assert.NotNull(normalized);
        AssertImportable(normalized);
    }

    [Fact]
    public void NormalizePrivateKey_Base64OfWholePemFile_Importable()
    {
        // kubectl-style: the whole .p8 file base64-encoded.
        var (pem, _) = CreateKey();
        var normalized = AuthenticationBuilderExtensions.NormalizePrivateKey(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)));
        Assert.NotNull(normalized);
        AssertImportable(normalized);
    }

    [Fact]
    public void NormalizePrivateKey_BarePkcs8Base64Body_WrappedAndImportable()
    {
        // The .p8 with its armor lines stripped — just the base64 body.
        var (_, pkcs8) = CreateKey();
        var normalized = AuthenticationBuilderExtensions.NormalizePrivateKey(
            Convert.ToBase64String(pkcs8));
        Assert.NotNull(normalized);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", normalized);
        AssertImportable(normalized);
    }

    [Fact]
    public void NormalizePrivateKey_Garbage_PassedThroughForImportToReport()
    {
        // Not silently swallowed: ImportFromPem gets the value and names the problem.
        Assert.Equal("not-a-key", AuthenticationBuilderExtensions.NormalizePrivateKey("not-a-key"));
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddAppleAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddAppleAuthentication_WithoutClientId_RegistersNoScheme()
    {
        await using var provider = BuildProvider([]);
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync("Apple"));
    }

    [Fact]
    public async Task AddAppleAuthentication_WithPrivateKey_GeneratesClientSecretFromKey()
    {
        var (pem, _) = CreateKey();
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Authentication:Apple:ClientId"] = "cloud.meshweaver.memex.signin",
            ["Authentication:Apple:TeamId"] = "TEAM123456",
            ["Authentication:Apple:KeyId"] = "KEY1234567",
            ["Authentication:Apple:PrivateKey"] = pem.Replace("\n", "\\n"),
        });

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemes.GetSchemeAsync("Apple");
        Assert.NotNull(scheme);
        Assert.Equal(typeof(AppleAuthenticationHandler), scheme.HandlerType);

        var options = provider.GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>().Get("Apple");
        Assert.True(options.GenerateClientSecret);
        Assert.Equal("TEAM123456", options.TeamId);
        Assert.Equal("KEY1234567", options.KeyId);
        Assert.NotNull(options.PrivateKey);

        // The delegate must hand the secret generator an importable PEM.
        var delivered = await options.PrivateKey("KEY1234567", CancellationToken.None);
        AssertImportable(delivered.ToString());
    }

    [Fact]
    public async Task AddAppleAuthentication_WithStaticSecretOnly_FallsBackToClientSecret()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Authentication:Apple:ClientId"] = "cloud.meshweaver.memex.signin",
            ["Authentication:Apple:ClientSecret"] = "externally.minted.jwt",
        });

        var options = provider.GetRequiredService<IOptionsMonitor<AppleAuthenticationOptions>>().Get("Apple");
        Assert.False(options.GenerateClientSecret);
        Assert.Equal("externally.minted.jwt", options.ClientSecret);
    }
}
