---
Name: Instance Secrets
Category: Architecture
Description: Secrets a global administrator enters in the portal instead of an operator minting them in a vault. They are stored encrypted in the instance's own mesh, used live without a restart, and never read back. Also the platform's write-only secret control, the fingerprint rule, and the slots that decide which settings the portal may set.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Instance Secrets

**A secret is entered through a WRITE-ONLY page, never through a terminal.** A person sets or
replaces a value, and afterwards the page shows only whether a value is set, its state, its
fingerprint, and who set it and when. No page, API or log ever shows the value again.

Two stores sit behind that one rule:

| store | where the value lives | who writes it | when a pod picks it up |
|---|---|---|---|
| **the vault** | the deployment's Key Vault, mounted into the pod as an environment variable | the operator, through a governed action run as system | at the next pod start |
| **the instance's own store** (this page) | the instance's mesh, `Admin/Secret-{key}`, encrypted with the instance's master key | a global administrator of THAT instance, in its own portal | at once, with no restart |

Use the instance's own store when the person who must enter the value administers only that
instance, for example a customer entering a key the control instance issued, or when the value
must take effect without a restart. Use the vault for everything the chart mounts at start.

## What is stored, and where

One node per configuration key, at `Admin/Secret-{key}`, with `:` written as `--`
(`Hosting:ControlInbox:Secret` → `Admin/Secret-Hosting--ControlInbox--Secret`). The content
(`InstanceSecretContent`) holds:

- the value, as `enc:`-tagged AES-256-GCM ciphertext from the platform key protector
  (`IProviderKeyProtector`, master key `Ai:KeyProtection:MasterKey`);
- its fingerprint, how it was produced (`paste` or `generate`), and who set it and when;
- its state: disabled, or deleted and recoverable until a date;
- during a rotation, the previous value (also encrypted) and when it stops being accepted;
- its last use: when, whether it succeeded, and one sentence about it.

🚨 **Encrypted or refused.** A value the protector does not return `enc:`-tagged is refused and
nothing is written. An instance with no master key therefore cannot store a secret at all, which
is the intended answer: the alternative would be a secret in the clear in the database.

## Which settings the portal may set: slots

The portal can set a key only if the code that READS the key registered a slot for it:

```csharp
builder.AddInstanceSecretSlot("Hosting:ControlInbox:Secret");     // exactly this key
builder.AddInstanceSecretSlot("Hosting:PlatformWebhookSecret:*"); // one more segment below it
```

A `:*` slot admits exactly one more segment of letters, digits, `-`, `_` or `.`, and never the
section key itself. So the slot above lets the control instance hold a per-deployment
announcement key (`Hosting:PlatformWebhookSecret:fabrikam`) and can never replace the fleet-wide inbox
secret (`Hosting:PlatformWebhookSecret`). A key no slot admits is refused, so the portal can never
override an arbitrary setting such as `Auth:GlobalAdmins`.

## Who may change a secret

Every verb checks rights first, as the CALLER: the caller must be a global administrator
(`hub.IsGlobalAdmin`). Only then does it write, as system. That way the Admin partition's own
curation never decides, and `setBy` still records the person. A refused call writes nothing and
says why.

## Reading: live, with no restart

`InstanceSecretCatalog` is a mesh-scoped singleton that keeps ONE query subscription
(`namespace:Admin nodeType:InstanceSecret`, as system). It decrypts each entry into the process's
memory and hands values only to the code that signs or verifies with them:

- `InstanceSecrets.Resolve(hub, key)` returns the value a SIGNER uses: the portal's value if one is
  set; nothing if it is disabled; otherwise the deployment configuration's value (also after a
  deletion).
- `InstanceSecrets.Candidates(catalog, configuration, section)` returns every per-sender key a
  VERIFIER accepts under a section, current value first, then the rotation's previous value.

A value saved in the portal therefore reaches the next signature or verification on every
replica, as soon as the change feed delivers it. `WhenLoaded` lets a verifier wait for the first
listing after a start, so the first delivery is not checked against configuration alone.

**Precedence.** A portal entry wins over a configured value for the same key. A DISABLED entry is a
tombstone: it also suppresses the configured value, so revoking a key in the portal stops it at
once, even if a vault mount still carries it. A DELETED entry gives way, and the configured value
applies again.

## The verbs

| verb | effect |
|---|---|
| `Set(key, value)` | stores a pasted value (`source: paste`). With `keepPreviousFor`, the replaced value keeps verifying for that long: a rotation. |
| `Generate(key)` | mints a 256-bit key on the server, stores it (`source: generate`) and returns it ONCE, so it can be shown to the person who asked. |
| `Disable` / `Enable` | stops or resumes use; the value is kept. |
| `Delete` / `Recover` | removes the portal's value (recoverable for `RecoveryWindow`) and undoes that. |
| `RecordUse(key, ok, result)` | records the last use; written as system by the signer or verifier. When a delivery first verifies with the CURRENT value, a pending rotation ends and the previous value is dropped. |

## The fingerprint

`SecretFingerprint.Of(value)` is the one rule fleet-wide. The operator computes the same value for
the vault tag `mw-fp`, so a fingerprint shown in the portal and one read from a vault must agree:

- the alphabet is 26 if the value has any `a–z`, plus 26 if any `A–Z`, plus 10 if any `0–9`, plus
  32 if any other byte;
- the strength is `floor(byteLength × log2(alphabet))` bits;
- at 128 bits or more the fingerprint is `sha256:` followed by the first 12 lowercase hex digits of
  SHA-256 over the UTF-8 bytes; below that it is `withheld:low-entropy`.

A generated key (64 hex digits, about 330 bits) always has a fingerprint. A human password does
not, because a public unkeyed hash of a weak value lets anyone test guesses against it.

## The write-only control

`WriteOnlySecretSection.Render(host, spec)` is the platform's ONE control for entering a secret. It
renders the status, a password box, and a button for each verb the spec provides. The spec's verbs
are `Save`, `Generate`, `Disable`, `Enable`, `Delete` (after a confirmation) and `Recover`.

- The password box's buffer is cleared the moment it is read. The value is trimmed, because a value
  pasted from an e-mail usually ends in a newline.
- A generated value is shown ONCE, in a dialog that says it will not be shown again. Closing the
  dialog removes it from the layout.
- The section reads back only a `SecretStatus`, whose JSON names are the operator's `kv_status`
  fields: `name`, `present`, `enabled`, `created`, `updated`, `expires`, `deleted`,
  `recoverableUntil`, `fingerprint`, `setBy`, `setAt`, `source`.

`SecretInventorySection.Render(host, id, statuses, verbsFor)` shows a list of secrets: a table of
every status, then one section per secret. It is the same control whether the list comes from a
vault inventory or from this store.

## Where it is used

- **The self-update announcement key.** The deployment's administrator GENERATES the key on the
  deployment under **/Admin/Settings/ControlLane**; the control instance registers that value as the
  deployment's per-sender key. See [Self-Update Announcement Key](../SelfUpdateAnnouncementKey).

## Related

- [Self-Update Announcement Key](../SelfUpdateAnnouncementKey): the first user of this store.
- [Access Control](../AccessControl): what a global administrator is.
