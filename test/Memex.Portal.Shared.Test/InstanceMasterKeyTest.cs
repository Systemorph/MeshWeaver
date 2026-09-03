using MeshWeaver.AI;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The install's key-protection master key. Two of these are the difference between "encrypted at
/// rest" and a credential leak, and one is the difference between an instance that can read its own
/// secrets after a restart and one that silently cannot.
/// </summary>
public class InstanceMasterKeyTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-key-" + Guid.NewGuid().ToString("N"));

    public InstanceMasterKeyTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void WithNoKeyAnywhere_ResolveAnswersNull_AndNeverMintsOne()
    {
        // 🚨 A read that silently minted a key would make "the deployment's Secret did not reach
        // the pod" indistinguishable from "this install has no secret store" — and a portal that
        // quietly re-keys cannot decrypt anything it wrote before. Minting is EnsureCreated's job,
        // reached only from the setup surface.
        Assert.Null(InstanceMasterKey.Resolve(root, configured: null));
        Assert.False(File.Exists(InstanceMasterKey.PathFor(root)));
    }

    [Fact]
    public void TheDeploymentsOwnKeyWins_AndIsNeverWrittenToDisk()
    {
        // On Kubernetes the key comes from a Secret and only the ciphertext is on the PVC. Writing
        // it to the volume would put both halves in the same place and undo that separation.
        var key = InstanceMasterKey.EnsureCreated(root, configured: "from-the-deployment");

        Assert.Equal("from-the-deployment", key);
        Assert.False(File.Exists(InstanceMasterKey.PathFor(root)));
    }

    [Fact]
    public void ABlankConfiguredKey_ReadsAsUnset()
    {
        // An environment variable cannot be null, only empty — so "" legitimately means "unset",
        // and treating it as a key would derive one from nothing and encrypt everything under it.
        var key = InstanceMasterKey.EnsureCreated(root, configured: "   ");

        Assert.NotEqual("   ", key);
        Assert.True(File.Exists(InstanceMasterKey.PathFor(root)));
    }

    [Fact]
    public void EnsureCreated_IsIdempotent_BecauseReKeyingStrandsEverySecret()
    {
        var first = InstanceMasterKey.EnsureCreated(root, configured: null);
        var second = InstanceMasterKey.EnsureCreated(root, configured: null);

        Assert.Equal(first, second);
        Assert.Equal(first, InstanceMasterKey.Resolve(root, configured: null));
    }

    [Fact]
    public void AMintedKey_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix modes do not exist there; the inherited ACL is the equivalent control.

        InstanceMasterKey.EnsureCreated(root, configured: null);
        var mode = File.GetUnixFileMode(InstanceMasterKey.PathFor(root));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void AMintedKey_IsAes256SizedAndRandom()
    {
        var a = InstanceMasterKey.EnsureCreated(root, configured: null);
        Assert.Equal(32, Convert.FromBase64String(a).Length);

        var other = Path.Combine(Path.GetTempPath(), "mw-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.NotEqual(a, InstanceMasterKey.EnsureCreated(other, configured: null));
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void LiteralAndConfigProviders_DeriveTheSameKey()
    {
        // 🚨 The setup surface encrypts through LiteralMasterKeyProvider; every later read goes
        // through ConfigMasterKeyProvider. A different derivation over the same configured value
        // yields a different AES key, so the secret would decrypt to nothing: the protector answers
        // the ciphertext unchanged, the projection drops it as unusable, and the provider silently
        // never registers. Nothing else in the system would notice.
        const string configured = "a-passphrase-not-base64";

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ConfigMasterKeyProvider.ConfigKey] = configured,
                })
                .Build())
            .BuildServiceProvider();

        var fromConfig = new ConfigMasterKeyProvider(services).GetMasterKey();
        var fromLiteral = new LiteralMasterKeyProvider(configured).GetMasterKey();

        Assert.NotNull(fromConfig);
        Assert.Equal(fromConfig, fromLiteral);
    }

    [Fact]
    public void ASecretWrittenAtSetup_IsReadableByTheNextBoot()
    {
        // The end-to-end property the two providers exist to guarantee: encrypt with the key the
        // wizard minted, restart, resolve the key from the file, decrypt.
        var minted = InstanceMasterKey.EnsureCreated(root, configured: null);
        var atSetup = new ProviderKeyProtector(new LiteralMasterKeyProvider(minted));
        var stored = atSetup.Protect("sk-ant-secret");

        Assert.StartsWith("enc:v1:", stored);

        var atNextBoot = new ProviderKeyProtector(
            new LiteralMasterKeyProvider(InstanceMasterKey.Resolve(root, configured: null)));

        Assert.Equal("sk-ant-secret", atNextBoot.Unprotect(stored));
    }
}
