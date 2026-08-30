using System;
using System.Reactive;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The registry-side half of an instance-key ROTATION driven from outside the registry process —
/// by the Hosting operator's <c>hosting-kv-rotate</c> job, which mints the raw <c>mwi_</c> key, puts
/// it straight into Key Vault, and reports ONLY its SHA-256 back through the job log.
///
/// <para>🚨 This contract carries hashes, never keys. The registry stores hashes
/// (<see cref="MeshWeaverInstance.KeyHash"/>), the operator holds the vault write, and nothing in
/// between logs, displays or persists the secret. It lives in the contract assembly so in-mesh
/// code (the Hosting plugin's control plane, compiled at runtime inside the registry portal) can
/// resolve it from the hub's service provider without referencing the portal host.</para>
/// </summary>
public interface IInstanceKeyRegistry
{
    /// <summary>
    /// Points the instance registered as <paramref name="instanceId"/> at a new key by its hash:
    /// the instance node takes <paramref name="keyHash"/> and a fresh issued-at, a fresh index
    /// entry resolves the hash to the instance, and the PREVIOUS index entry is deleted so the old
    /// key stops authenticating the moment this completes. Idempotent for a hash already adopted.
    /// </summary>
    /// <param name="instanceId">The instance's id — the value <c>PluginCatalog__InstanceId</c> carries.</param>
    /// <param name="keyHash">Lowercase SHA-256 hex (64 chars) of the new raw key.</param>
    IObservable<Unit> AdoptKeyHash(string instanceId, string keyHash);
}
