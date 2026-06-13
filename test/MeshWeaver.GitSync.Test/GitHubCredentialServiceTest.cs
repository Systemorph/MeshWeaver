using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The per-user GitHub credential is stored encrypted at rest and read back decrypted.
/// </summary>
public class GitHubCredentialServiceTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 60000)]
    public void Save_Then_Get_RoundTripsDecryptedToken()
    {
        Connect(token: "ghp_secret_ABC123", login: "octocat");
        var cred = WaitForCredential();

        Assert.Equal("ghp_secret_ABC123", cred.AccessToken);
        Assert.Equal("octocat", cred.GitHubLogin);
        Assert.Equal("repo", cred.Scopes);
    }

    [Fact(Timeout = 60000)]
    public void StoredToken_IsEncryptedAtRest()
    {
        Connect(token: "ghp_secret_ABC123");

        var node = WaitForNode(GitHubCredentialService.CredentialPath(UserId));
        var stored = ExtractCredential(node);

        Assert.NotNull(stored);
        Assert.StartsWith("enc:v1:", stored!.AccessToken);
        Assert.DoesNotContain("ghp_secret_ABC123", stored.AccessToken);
        // The protector decrypts the stored blob back to the plaintext.
        Assert.Equal("ghp_secret_ABC123", Protector.Unprotect(stored.AccessToken));
    }

    [Fact(Timeout = 60000)]
    public void Delete_RemovesCredential()
    {
        Connect();
        Credentials.Delete(UserId).Timeout(30.Seconds()).Wait();
        Assert.True(IsAbsent(GitHubCredentialService.CredentialPath(UserId)));
    }

    private GitHubCredential? ExtractCredential(MeshNode node) => node.Content switch
    {
        GitHubCredential c => c,
        JsonElement je => JsonSerializer.Deserialize<GitHubCredential>(je.GetRawText(), Mesh.JsonSerializerOptions),
        _ => null,
    };
}
