using System;
using System.Security.Cryptography;
using System.Text;
using Memex.Portal.Shared.Social;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A delivery signed with the instance's OWN GitHub App secret reaches the push trigger only. That
/// secret is held by the instance's administrators, a platform role with no data rights, so a forged
/// <c>issues</c>/<c>issue_comment</c>/<c>workflow_run</c> delivery signed with it must never reach
/// the processors that write into Spaces as System. The deployment's secret keeps every event.
/// </summary>
public class GitHubWebhookSignerScopeTest
{
    private const string DeploymentSecret = "deployment-secret";
    private const string AppSecret = "instance-app-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"repository":{"full_name":"Contoso/records"}}""");

    private static string Sign(string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Body)).ToLowerInvariant();

    [Theory]
    [InlineData("issues")]
    [InlineData("issue_comment")]
    [InlineData("workflow_run")]
    public void AnInstanceAppSignedDelivery_OfANonPushEvent_IsRefused(string eventType)
    {
        var signer = GitHubWebhookEndpoints.VerifiedSigner(DeploymentSecret, AppSecret, Body, Sign(AppSecret));
        Assert.Equal(GitHubWebhookEndpoints.Signer.InstanceApp, signer);
        Assert.False(GitHubWebhookEndpoints.SignerMayDeliver(signer!, eventType));

        // Control: the same event signed with the deployment's secret is admitted.
        var deployment = GitHubWebhookEndpoints.VerifiedSigner(DeploymentSecret, AppSecret, Body, Sign(DeploymentSecret));
        Assert.Equal(GitHubWebhookEndpoints.Signer.Deployment, deployment);
        Assert.True(GitHubWebhookEndpoints.SignerMayDeliver(deployment!, eventType));
    }

    [Theory]
    [InlineData("push")]
    [InlineData("ping")]
    public void AnInstanceAppSignedPush_IsAdmitted(string eventType)
    {
        var signer = GitHubWebhookEndpoints.VerifiedSigner(null, AppSecret, Body, Sign(AppSecret));
        Assert.Equal(GitHubWebhookEndpoints.Signer.InstanceApp, signer);
        Assert.True(GitHubWebhookEndpoints.SignerMayDeliver(signer!, eventType));
    }

    [Fact]
    public void ADeliverySignedByNeither_HasNoSigner()
        => Assert.Null(GitHubWebhookEndpoints.VerifiedSigner(DeploymentSecret, AppSecret, Body, Sign("someone-else")));
}
