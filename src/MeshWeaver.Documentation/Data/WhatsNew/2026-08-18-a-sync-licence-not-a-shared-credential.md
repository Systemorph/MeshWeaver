---
Name: A sync licence, not a shared credential
Category: Feature
Description: An instance's right to replicate a package is now a licence with terms, a term and an issuer — and it can be exchanged for a short-lived, scoped token, so a consumer never holds a durable credential.
Icon: Key
Order: -20260818
---

# A sync licence, not a shared credential

The registry has always been able to say *which* packages a registered instance may pull. What it
could not say is **under what terms, until when, and on whose authority** — a `PluginGrant` was a
list of `Source/Package` entries and nothing else, written in exactly two places: the registration
seed, and an admin typing into the Instance-grants tab.

That is fine for three instances. It is not fine for a catalogue, and the pressure showed up the
way these things usually do: a consumer that needed one package asked for a standing credential to
the whole repository, because there was no smaller thing to ask for.

## The grant is now the licence

`PluginGrant` keeps doing exactly what it did — the registry surface still calls `Allows(source,
package)` and every existing grant keeps working unchanged — but each entry now carries the terms
it was issued under:

- **`ExpiresAt`** — the term. Null stays perpetual, which is why nothing written before this change
  behaves differently. It sits on the ENTRY rather than the grant because one instance routinely
  holds a perpetual licence for the platform repo alongside a termed one for a paid package.
- **`IssuedUnderLicense`** — the SPDX id, resolving to a `License` node whose text can actually be
  shown. Unspecified stays null; a licence nobody granted is never invented.
- **`IssuedVia`** — an order, a coupon, a ticket. The audit trail for a right that is otherwise
  indistinguishable from any other.
- **`IsRevoked`** on the grant — the instance-wide stop, which keeps the entries intact so a
  revocation can be reviewed afterwards and lifted.

`SyncLicenseService` is now the one writer. Issuing is idempotent, revocation is a flag rather than
a deletion, and an issuance with no issuing principal is refused rather than written anonymously —
a right nobody is recorded as having granted cannot be reviewed with confidence.

## And licences that ask something are now enforced

`LicenseContent.RequiresAcceptance` and the per-user `LicenseAcceptance` record have existed as node
types for a while, with a body hash so an acceptance is evidence against the text that was actually
shown. Nothing read them. They are now enforced on the install path, beside the entitlement check
and for the same reason — on the action, so the unattended paths are gated identically to a click.

The body hash is checked, not merely stored: an acceptance recorded against earlier terms does not
satisfy revised ones. Normalization ignores what a round-trip through git or an editor changes and
nothing else, so consent is not revoked by an invisible line ending.

Permissive licences ask nothing and gate nothing — Apache-2.0 and MIT install exactly as before.

## Exchange the durable key for a token that expires

`POST /api/instances/token` trades an instance's durable `mwi_` key for a short-lived `mwa_` token,
narrowed to the packages it actually needs:

```
POST /api/instances/token
Authorization: Bearer mwi_…
{ "scope": ["Plugins/Publish"], "lifetimeSeconds": 900 }

→ { "accessToken": "mwa_…", "tokenType": "Bearer", "expiresIn": 900,
    "scope": ["Plugins/Publish"] }
```

Three properties make it safe to hand out freely:

- **It carries identity and scope, never authority.** The live licence is re-read on every request,
  so revoking one takes effect immediately rather than when the token expires.
- **It can only narrow.** A token minted for more than its licence covers grants nothing extra.
- **A token cannot mint its successor.** Only the durable key may exchange, or a minutes-long
  credential would become perpetual by renewal.

The token itself is signed rather than stored, so minting writes no node and there is no expiry
sweep to maintain — which is only safe *because* authority is re-checked on use.

**The signing key is a mesh node**, minted on first use at `Admin/SyncTokenSigningKey/current` and
`enc:`-protected at rest. Nothing to configure, nothing for an operator to copy between
environments, and no human ever sees it. Uniqueness across replicas comes from the node: two racing
to mint collide on one fixed path, storage keeps the first, and — because the create response tells
*both* callers they won — each reads the stored key back and signs with that. Rotation keeps the
outgoing key verifiable for one token lifetime, so rotating never invalidates work in flight.

The point of all of it: a consumer that needs one package can now hold a credential for one package,
for fifteen minutes.
