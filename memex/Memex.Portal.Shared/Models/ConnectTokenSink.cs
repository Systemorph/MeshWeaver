using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Connect;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Models;

/// <summary>
/// Portal-side implementation of <see cref="IConnectTokenSink"/>: persists a captured CLI
/// subscription token as an encrypted <c>ModelProvider</c> node via <see cref="ModelProviderService"/>
/// (create on first connect, rotate on re-connect). Lives in the portal so the AI layer
/// (<c>ConnectSessionManager</c>) never references the portal assembly — the seam is the interface.
///
/// <para>Reactive end-to-end (no <c>Task</c>): create / rotate return cold observables and we
/// project their result into <c>(providerNodePath, keyFingerprint)</c>.</para>
/// </summary>
public sealed class ConnectTokenSink(ModelProviderService providerService, ILogger<ConnectTokenSink> logger)
    : IConnectTokenSink
{
    public IObservable<(string ProviderNodePath, string KeyFingerprint)> StoreToken(
        string ownerPath, string providerName, string token)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Throw<(string, string)>(new ArgumentException("ownerPath required", nameof(ownerPath)));
        if (string.IsNullOrEmpty(token))
            return Observable.Throw<(string, string)>(new ArgumentException("token required", nameof(token)));

        var fingerprint = ConnectSessionManager.Fingerprint(token);
        var providerPath = $"{ownerPath}/{ModelProviderNodeType.RootNamespace}/{providerName}";

        // Create on first connect; if the provider node already exists, rotate its key. We avoid
        // GetProvidersForOwner().Take(1) here — that synced query is Replay(1).RefCount(), and the
        // .Take(1) tears the upstream subscription down again before the create lands, which can
        // wedge the brand-new partition's per-node hub. Instead: try create, fall back to rotate on
        // conflict (the same create-or-update shape SetSelection uses).
        logger.LogInformation("Connect: storing {Provider} key for {Owner} (fp={Fp})",
            providerName, ownerPath, fingerprint);
        return providerService.CreateProvider(ownerPath, providerName, token)
            .Select(result => (result.ProviderNode.Path ?? providerPath, fingerprint))
            .Catch<(string, string), Exception>(_ =>
                providerService.RotateKey(providerPath, token)
                    .Select(_ => (providerPath, fingerprint)));
    }
}
