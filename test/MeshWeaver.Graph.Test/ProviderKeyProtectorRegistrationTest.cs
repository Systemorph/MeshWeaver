using MeshWeaver.AI;   // IProviderKeyProtector & co keep their ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The credential protector must exist on a mesh built from the PLATFORM alone.
///
/// <para>It used to be registered by <c>AddModelProviderType()</c>, i.e. by <c>AddAI()</c>. That was
/// invisible while the AI engine shipped in every image — but four unrelated subsystems resolve it
/// OPTIONALLY (<c>GetService</c>, null ⇒ store the secret verbatim): a model provider's ApiKey, a
/// GitHub PAT, the Entra EA credential, and the plugin catalog's sync-token signing key. So the
/// day the engine leaves for a module (#2276), a deployment without it would have written all four
/// in PLAINTEXT and said nothing — no exception, no warning, and a stored secret that still
/// "works" because Unprotect passes untagged values through.</para>
///
/// <para>The registration now lives in <c>AddGraph()</c> and this is the ratchet: exactly ONE
/// registration exists in the tree, so resolving it here proves the platform path carries it.</para>
///
/// <para>🚨 "Optionally, null ⇒ store the secret verbatim" is no longer the fallback anywhere —
/// those four call sites now resolve the protector as REQUIRED, and <c>Protect</c> itself refuses
/// rather than passing plaintext through when no master key is configured. So this test asserts
/// the value that registration is FOR: the stored form is ciphertext, not the input.</para>
/// </summary>
public class ProviderKeyProtectorRegistrationTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public void AddGraph_RegistersTheCredentialProtector()
    {
        var protector = Mesh.ServiceProvider.GetService<IProviderKeyProtector>();
        var masterKey = Mesh.ServiceProvider.GetService<IMasterKeyProvider>();

        protector.Should().NotBeNull(
            "every consumer resolves this optionally and falls back to storing the secret VERBATIM — "
            + "an absent registration is silent plaintext at rest, not an error");
        masterKey.Should().BeOfType<ConfigMasterKeyProvider>(
            "the default reads Ai:KeyProtection:MasterKey from configuration; a deployment swaps in "
            + "a KMS-backed provider by registering its own before AddGraph runs");

        const string secret = "ghp_the_committing_users_token";
        var stored = protector!.Protect(secret);

        // 🚨 The STORED form is ciphertext. This used to read "round-trips whether or not this host
        // configures a master key … without, both directions are passthrough" — and a round-trip
        // alone passes on a passthrough, which is the behaviour that put a live key in cleartext
        // into production. Asserting the stored bytes is what a passthrough cannot satisfy.
        stored.Should().StartWith("enc:v1:").And.NotContain(secret);
        protector.Unprotect(stored).Should().Be(secret);
    }
}
