---
Name: Secret Sealing
Category: Architecture
Description: How an installation hands a secret to its control instance without the value ever being plaintext in a node, an event or a log. An ECDH P-256 + HKDF-SHA256 + AES-256-GCM envelope, sealed to a public key the control instance publishes, bound to the deployment and object it is for, with key rotation.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Secret Sealing

An installation's administrator types a secret into their OWN portal. For the secret to reach the control
instance (to be written to the vault by a governed action) it must travel through nodes and signed events. It does so
as **ciphertext only**: the installation's portal *seals* it to a public key the control instance publishes, and only
the control instance opens it.

## The envelope

`SecretEnvelope` (`MeshWeaver.Mesh.Contract`), version 1, .NET built-ins only:

- **ECDH P-256** with a fresh ephemeral sender key per seal,
- **HKDF-SHA256** over the shared secret (random 16-byte salt, info `mw-secret-envelope v1`),
- **AES-256-GCM** with a random 12-byte nonce.

The associated data is `mw-secret-envelope v1`, then the key id and the context, each as a 4-byte big-endian
byte length followed by its UTF-8 bytes. The length prefixes make it unambiguous: moving a `|` from the key id
into the context (`k1|a` + `b` against `k1` + `a|b`) is different associated data and does not authenticate. `Seal`
refuses a key id its own reader would refuse (1 to 64 characters). For a vault write the context is
`{deployment}|{object}`, so a sealed value **cannot be replayed onto another deployment or object**: unsealing
with a different context fails authentication, like a wrong key, a wrong key id, a different version or any changed
byte. `Unseal` answers `null` for every failure, never an exception that could say which.

Wire form: `{ "v":1, "kid":"…", "epk":"…", "salt":"…", "nonce":"…", "ct":"…", "tag":"…" }`.

**Hostile input is refused before anything is decoded.** `TryParse` reads at most 16 384 characters, and both
`TryParse` and `Unseal` check every field against what a sealed envelope can carry (a P-256 SPKI key, 16/12/16 random
bytes, 1..8192 bytes of ciphertext), so an envelope with a bad tag cannot make the control instance allocate for it.

## The control instance's key

`SecretSealingKeyService` (`MeshWeaver.PluginCatalog`) owns one node, `Admin/SecretSealingKey/current`, minted by the
control plane itself (never by a request) and read back so replicas agree. It holds the **public key in the clear** and the **private key only as `enc:` ciphertext of the
platform key protector**, refused when no master key exists.

**Rotation:** every 90 days, or on demand. Each outgoing key stays accepted for 7 days from its own rotation, so a
value sealed just before the rotation still opens; after that only the newer keys do. **Every** retired key inside its
window is kept (`SecretSealingKey.Retired`), so two rotations within one window never drop the first outgoing key
early.

The due-check and the rotation happen together, inside ONE `GetMeshNodeStream(path).Update`. The lambda runs on the
writer's mirror, and what reaches the owning hub is the merge patch of what it changed plus the writer's base values,
which the owner merges three-way. A request whose mirror was behind is therefore refused on the current key's fields
(they changed on the owner since its base), re-reads, finds the rotated key not due and adopts it; every caller
answers with the key as stored, never its own proposal. `Retired` is never null, so it always carries a base and
the owner merges it as a list: a rotation delayed behind two others appends to the retired keys and never replaces
them.

**Opening and rotating are authorized as the caller, then executed as System**
([Authorize as the caller, execute as System](../AuthorizeAsCallerExecuteAsSystem)). The service is a mesh
singleton, so any code holding a hub can resolve it, in-mesh NodeType source included. `Unseal` requires `Execute`
and `Rotate` requires `Update` on `Admin/SecretSealingKey/current`, checked for the caller's own identity before
anything is elevated. A caller without the right gets `UnauthorizedAccessException`. A check that could not be
decided fails closed.

## Publishing the public key

`GET /api/instances/seal-key` is anonymous and read-only: it is a public key, and an installation that holds only its
announcement key (the control-inbox credential) must be able to fetch it. It answers `{ keyId, publicKey, algorithm,
version }`, or 404 until the control plane has minted the pair, or a logged 503 when the key cannot be read. Only a
read that answers *absent* counts as "no key"; a delete in flight or an unanswered read is a fault, so no caller ever
mints a second key over a stored one. It
authenticates nobody, so an answer from it is never an authentication boundary. A request can never provoke the mint, and nothing else
leaves the control instance. What the key is *for* is gated elsewhere: a value sealed to it is accepted only inside an
event signed with the deployment's own key.

## Who uses it

The governed secret write for an installation's own record (Hosting, `instance-secret-write`): the installation seals
the value for `{deployment}|{vault object}`, the signed event carries the envelope, and the control plane unseals it
into the ONE operator Job's environment, scrubbing the ciphertext when the run ends.
