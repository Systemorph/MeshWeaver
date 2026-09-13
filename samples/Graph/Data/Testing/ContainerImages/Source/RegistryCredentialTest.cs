// <meshweaver>
// Id: Testing/ContainerImages/RegistryCredentialTest
// DisplayName: Testing/ContainerImages/RegistryCredentialTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Text;

/// <summary>
/// A registry client presents its credential in TWO shapes — <c>Basic</c> to the token endpoint,
/// <c>Bearer</c> everywhere after — and this is the one place the mirror reduces them to the
/// single value it authenticates. Getting it wrong is silent in the worst direction: a truncated
/// secret fails to authenticate for a reason no log explains, and a too-permissive read lets a
/// malformed header through as if it carried something.
/// </summary>
public class RegistryCredentialTest
{
    private static string Basic(string user, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));

    [MeshFact]
    public void ReadsABearerVerbatim() =>
        Assert.Equal("mw_key_abc", RegistryCredential.TryReadSecret("Bearer mw_key_abc"));

    [MeshFact]
    public void ReadsThePasswordHalfOfABasicCredential() =>
        Assert.Equal("mw_key_abc", RegistryCredential.TryReadSecret(Basic("ci", "mw_key_abc")));

    /// <summary>
    /// 🚨 The FIRST colon separates user from secret. An ACR refresh token contains colons, and a
    /// last-colon (or split-on-all) reading would hand back a truncated secret — which
    /// authenticates as "wrong credential", the one failure mode with no diagnostic.
    /// </summary>
    [MeshFact]
    public void SplitsOnTheFirstColonOnly_SoASecretMayContainColons() =>
        Assert.Equal("a:b:c", RegistryCredential.TryReadSecret(Basic("ci", "a:b:c")));

    /// <summary>
    /// <c>docker login -u &lt;anything&gt;</c> is how a client is made to send an instance key, so
    /// the username half carries no meaning and must not be able to change the outcome.
    /// </summary>
    [MeshTheory]
    [MeshInlineData("ci")]
    [MeshInlineData("00000000-0000-0000-0000-000000000000")]
    [MeshInlineData("")]
    public void TheUsernameHalfIsDiscarded(string user) =>
        Assert.Equal("mw_key_abc", RegistryCredential.TryReadSecret(Basic(user, "mw_key_abc")));

    [MeshTheory]
    [MeshInlineData(null)]
    [MeshInlineData("")]
    [MeshInlineData("   ")]
    [MeshInlineData("Bearer")]                 // scheme with no value
    [MeshInlineData("Bearer ")]
    [MeshInlineData("Basic ")]
    [MeshInlineData("Basic not-base64!!")]     // undecodable
    [MeshInlineData("Negotiate abc")]          // a scheme the mirror does not speak
    [MeshInlineData("mw_key_abc")]             // a bare secret with no scheme
    public void RefusesEverythingElse_NullIsNeverAllow(string? header) =>
        Assert.Null(RegistryCredential.TryReadSecret(header));

    /// <summary>A Basic credential with no colon at all carries no secret half.</summary>
    [MeshFact]
    public void RefusesABasicPayloadWithNoColon() =>
        Assert.Null(RegistryCredential.TryReadSecret(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolonhere"))));

    /// <summary>An empty secret half is not a credential.</summary>
    [MeshFact]
    public void RefusesAnEmptySecretHalf() =>
        Assert.Null(RegistryCredential.TryReadSecret(Basic("ci", "")));

    [MeshTheory]
    [MeshInlineData("bearer mw_key_abc")]
    [MeshInlineData("BEARER mw_key_abc")]
    public void TheSchemeIsCaseInsensitive_AsHttpRequires(string header) =>
        Assert.Equal("mw_key_abc", RegistryCredential.TryReadSecret(header));

    /// <summary>The round trip the token exchange relies on: what the endpoint hands back, read
    /// as a bearer on the next request, is the same secret.</summary>
    [MeshFact]
    public void ABasicCredentialRoundTripsThroughTheBearerHeaderTheTokenEndpointIssues()
    {
        var secret = RegistryCredential.TryReadSecret(Basic("ci", "mw_key_abc"));
        Assert.NotNull(secret);
        Assert.Equal(secret, RegistryCredential.TryReadSecret(
            RegistryCredential.AsBearerHeader(secret!)));
    }
}
