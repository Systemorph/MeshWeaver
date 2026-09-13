---
Name: The Self-Update Registry Credential
Category: Architecture
Description: Which plugin-registry instance key the self-updater may present to a container registry, why host equality was the wrong rule for a fleet whose registry validates at another portal, and why the replacement is a declared pairing rather than a resemblance.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/><path d="M12 15v2"/></svg>
---

# The Self-Update Registry Credential

**An installation on a non-ACR registry lists its platform tags with the `mwi_` instance key it
already holds for a plugin registry. WHICH plugin registry's key is a disclosure decision, and it is
decided by DECLARED PAIRINGS — never by host equality, never by name resemblance, and never by "there
is only one key, so use it".**

🚨 **Two different questions, and confusing them is how this page would mislead.** The key is always
**SENT TO** `SelfUpdate:Registry` — the container registry — because that is the host being listed.
What the rule below decides is **WHICH KEY**: which of this installation's configured
plugin-registry credentials is selected. The validator host is a *credential source*, never a
destination; nothing is ever sent to it by the lister.

So, in `OciTagLister.ResolveCredential` — **selecting** the credential:

| The key selected is the one held for… | …because | the key is sent to |
|---|---|---|
| the container registry's **own host**, when a plugin registry is configured there | the operator already gave this installation a key for that exact host — a portal serving its images from the `/v2` mirror it also serves its catalog from | the container registry |
| the host named by **`SelfUpdate:RegistryValidationUrl`**, when a plugin registry is configured there | the operator declared that portal as the one that *validates* this installation's key at that registry, AND gave this installation a key for it | the container registry — **still**, never the validator |
| **nothing else, ever** | an absent declaration is a refusal, not permission | nothing is sent |

Both rows need TWO explicit statements. A declaration alone grants nothing (there is still no key for
that host); a key alone grants nothing (nothing says that registry trusts that portal).

### The SHAPE of the credential follows the row

🚨 **A declared validator is handed the DURABLE `mwi_` key, never an exchanged token.** The Store's
resolver (`RegistryTokenResolver.ResolveToken`) presents a configured `Token` as configured, but a
STORED key — the one an auto-registered installation holds after `PluginCatalog:BootstrapKey`, the
zero-touch shape `InstanceProvisioningPlan` documents — it first EXCHANGES for a short-lived `mwa_`
token at `/api/instances/token`. On the fleet's shape the declared validator **is** that exchange
endpoint, and it refuses a token by design (a token may never mint its successor;
`InstanceTokenEndpoints`). So a lister resolving through `ResolveToken` would present `mwa_…` to
`cr.meshweaver.cloud`, be refused on every check, and fix only the installations with a raw `Token`
configured — `build` and `pearl` — while the auto-registered ones stayed frozen (review of #4094).
The second row therefore resolves through `ResolveDurableKey`: the configured token, else the stored
key decrypted, **no exchange**. The first row is unchanged — a portal's own `/v2` mirror runs the same
authenticator the plugin registry does and accepts both shapes. `OciTagListerTest` pins it with the
auto-registered shape: the presented secret is the stored `mwi_` key and the exchange route is never
called; resolving through `ResolveToken` reports one exchange and a refused listing.

## 🚨 Measured: with the target check removed, the instance key reaches an arbitrary host — twice

Not an argument, a measurement. Delete the bare-host requirement, point `SelfUpdate:Registry` at
`instance:mwi_…@evil.example.test`, declare any validator this installation holds a key for, and the
test fixture's attacker host receives **two credentials**: the Basic credential at the token realm it
names, then the Bearer on the listing. `OciRegistryClient` builds `https://{registry}/` for a value
with no scheme, so that string is a *valid* URI whose host is `evil.example.test`.

**That is why the bare-host requirement is not negotiable, and why it is host-EQUALITY rather than
"parses to a host".** Everything below explains the shape; this is the reason it exists.

## This IS a trust expansion — say so

**Before this change, the instance key could reach only a host that had a plugin registry configured
on it. After it, the key can reach ANY host, provided some configured plugin registry is declared to
be that host's validator.** That is the feature working as designed — the fleet's registry is exactly
such a host — and it is fail-closed. But it widens who can receive a credential, and three things
bound it:

1. **An explicit declaration.** `SelfUpdate:RegistryValidationUrl` must name the validator. Absent,
   nothing changes; absence is never permission, and no resemblance of names substitutes for it.
2. **A bare-host target.** `SelfUpdate:Registry` must already be `host[:port]` (below).
3. **A plugin registry actually configured at the declared validator.** A declaration for a host this
   installation holds no key for grants nothing — there is no key to select.

Remove any one of the three and the expansion becomes unbounded. They are not defence in depth; each
one is load-bearing.

### The destination is validated first, and here is the attack that requires it

**`SelfUpdate:Registry` must already be a bare `host[:port]`.** `OciRegistryClient` builds
`https://{registry}/` for a value with no scheme, so `instance:mwi_…@evil.example` is a *valid* URI
whose host is `evil.example` — it receives the Basic credential, and the raw value is interpolated
into a dozen of that client's error messages besides.

🚨 **The fix introduced the vulnerability it exists to prevent.** Host equality could never match
`instance:mwi_…@evil.example`, so the old code always refused it. The declared-validator rule matches
on the *declared* host and never looks at the *target* — so the new trust path made a previously
unreachable value reachable. Measured, not argued: with the target check deleted, the test fixture's
attacker host received **two** credentials (Basic at the token realm, then the Bearer).

That is why the requirement is a BARE HOST and not merely "parses to a host". Do not simplify it
back to `HostOf(registry)`: normalizing silently would accept a URL form here while
`PortalImage`/`MigrationImage` — which interpolate the same value — stayed malformed, and the
constraint would have lost the reason it exists. A value that does not parse to an http(s) host is
refused **without being echoed**, because the userinfo is where a key would be.

🚨 **"Bare" is a textual test on the configured value, not equality with the parsed host.** `HostOf`
drops a scheme-default port, so a check written as `HostOf(value) == value` refused `cr.example.test:443`
— a legal `host:port` this page and the chart promise — with a message claiming it "carries a scheme
or a path" (review of #4094). The check is now: no scheme, no path, no query, no fragment (userinfo
was refused a line earlier); the client and the credential match then use the normalized host.

`SelfUpdateOptions.HostOf` is the platform's ONE registry-host rule, not this feature's. It used to
have a twin — `RegistryUpdateReconciler.SameRegistry` decided whether a `ModulePublished` broadcast
names a configured registry with its own reader, with different port semantics (`http://x:443` and
`https://x` were the same registry there and different hosts here). `SameRegistry` now reads through
`HostOf`, so the two subsystems agree: a scheme-default port is not part of the host, any other port
is, and a value that names no http(s) host — a bare non-URL, a `mailto:`, a URL carrying userinfo —
names no registry anywhere rather than matching a second copy of the same unreadable string.

## Why host equality was wrong

The fleet's registry deliberately does **not** hold credentials. `cr.meshweaver.cloud` decides a pull
by forwarding the caller's password to a portal — `docker_auth`'s `ext_auth` hook `POST`s it to
`registry.validationUrl`, by default `https://memex.meshweaver.cloud/api/instances/token`, and grants
`pull` on a 200 ([A Container Registry in Memex](../ContainerRegistryInMemex)). That is what makes ONE
`mwi_` key serve both the plugin catalog and the image pull, exactly as
`RegistrySpec.ValidationUrl` says it does:

> The registry's own pull is decided by the portal at `ValidationUrl` — the same instance-key gate
> the plugin registry uses — **so a consumer holds ONE credential for both.**

So the image registry and the plugin registry are **two different hosts by design**. A rule that
presents the key only to a host that is *itself* a plugin registry can therefore never authenticate
on the fleet's own shape. Measured 2026-09-12 on the first boot of `Deployments/build`
([#4093](https://github.com/Systemorph/MeshWeaver/issues/4093)): the kubelet pulled the image in six
seconds with that very key, and the self-updater in the same pod refused to use it —

```
[SelfUpdate] check (Startup): check FAILED: InvalidOperationException: SelfUpdate:Registry is
'cr.meshweaver.cloud' … but no plugin registry … is configured on that host
```

Every instance provisioned on the fleet registry booted fine and then never self-updated. `Reconcile`
and `Roll` from the control instance still worked, which is why the state is easy to miss: the
instance is healthy, it is merely frozen.

## Why the obvious fix is the wrong fix

The guard is a **credential-disclosure control**. `SelfUpdate:Registry` names a host; without the
check, whatever that host happens to be gets handed this installation's instance key. So none of
these is acceptable:

- **Drop the check** — any `SelfUpdate:Registry` value now receives the key.
- **"When exactly one consumer mount carries a key, present it"** — the number of mounts an
  installation has is not a statement about which registries may hold its key. It is the same rule as
  dropping the check for the common case of one mount, which is most of the fleet.
- **Same registrable domain / a suffix match** (`*.meshweaver.cloud`) — a coincidence of naming, not
  a grant. Whoever can obtain a name under the suffix obtains the key.

What makes the pairing safe is that the registry **declares** which portal it trusts with the key.
That declaration is already data — `RegistrySpec.ValidationUrl` — so the fix restates it on the
consuming side rather than inventing a new concept.

## Where the running portal learns the pairing

🚨 **It does not learn it by itself, and that is deliberate.** `RegistrySpec` lives on the record of
the instance that **HOSTS** the registry (`Deployments/memex-cloud`), on the control instance. A
CONSUMER's record (`build`, `pearl`) has no `registry` block at all, `HelmValues` renders
`registry.validationUrl` only for the hosting record, and reaching across instances to read it would
put a network dependency inside the credential path — a lookup that fails open, or fails the update.

So the pairing is an **explicit declaration the operator sets**, in the consumer's own configuration:

| where | what |
|---|---|
| `SelfUpdate:RegistryValidationUrl` | the config key the portal binds (`SelfUpdateOptions.RegistryValidationUrl`) |
| `selfUpdate.registryValidationUrl` in the chart | renders `SelfUpdate__RegistryValidationUrl` into the portal ConfigMap |
| a record's `extraPortalConfig` / an overlay's `config.memex_portal` | renders the same key — this is how an existing instance is fixed without a chart change |

The value is the registry record's `validationUrl`, copied verbatim
(`https://memex.meshweaver.cloud/api/instances/token`); a bare host means the same thing. **Only the
host is ever read**, and it is read whole: a non-default port is part of it, and a value carrying
userinfo (`https://memex.meshweaver.cloud@evil.example`) declares NOTHING rather than a pairing with
the host a human would not have read. An empty value — the default — declares nothing and refuses.

🚨 **"Declared" and "readable" are two questions, and the refusal says which one failed.** A value
that is SET but names no http(s) host — userinfo, a typo'd scheme, a `mailto:` — is refused as
**malformed** (`SelfUpdate:RegistryValidationUrl is set but does not name an http(s) host`, value not
echoed), never folded into "nothing declared": collapsing the two told the operator to declare the
key they had already set, a fail-closed fallback forging a correct-looking bug (review of #4094). The
boot line has the same three states: validated at `{host}`, SET but unreadable, NO validator
declared. `SelfUpdateOptions.RegistryValidatorDeclared` is the predicate; `RegistryValidatorHost` is
the reading.

### The alternative: derive it on the control instance — recorded, not done

The pairing could be **derived at render time** instead of hand-copied: `HelmValues` already derives
`selfUpdate.registry` from the image host, and the hosting record whose `registry.host` equals it
carries the `validationUrl`. One declaration on the registry's own record, no per-consumer copies,
still no network in the update path — the consumer-side key stays the wire; only who writes it
changes. It would also give `PluginBundleClient.DownloadArtifact`, which today presents the same key
to whatever host the catalog advertises with no declaration at all, the same rule as the self-updater
— one key/host pair currently lives under two trust rules. That is a real alternative to this page's
design, and it is deliberately NOT part of #4094: it moves where a trust rule lives.
[#4123](https://github.com/Systemorph/MeshWeaver/issues/4123) carries it, ordered after the
config-repo declaration that closes #4093.

## What the refusal says

The message names the hosts and the key that would declare the pairing. It never names, echoes,
lengths or logs the credential itself. The one new log line, at `Information` on the paired path
only, names the plugin registry, the container registry and the declared validator — so "who did this
installation hand its key to" is answerable from Loki, and a pairing nobody declared can never
produce a line.

Two diagnoses exist so that a wrong one is never given:

- **A plugin registry that EXISTS on the host, but whose URL carries credentials**
  (`https://instance:mwi_…@memex.meshweaver.cloud`). `HostOf` refuses userinfo, so such a registry
  matches on neither row — the pre-#4094 reader tolerated it — and without its own diagnosis the
  refusal would claim "no plugin registry is configured on that host" about a registry the catalog is
  already talking to. The message names the host, says the URL carries credentials, and names the fix
  (`PluginCatalog:Registries:N:Token`, bare `https://host`); the URL is not echoed. Refuse-and-name
  was chosen over matching the host: userinfo in a plugin-registry URL is never honoured as a
  credential by the catalog either (the HTTP client drops it), the catalog logs `registry.Url`
  verbatim on several pre-existing paths, and no record in the fleet uses the shape — so the right
  answer is to say where the credential belongs, not to read past it.
- **The boot line names the HOST read from `SelfUpdate:Registry`, never the configured value.** It
  is an `Information` line, written on every start BEFORE the lister's non-echoing refusal has ever
  run — so the pre-review line, which logged `_options.Registry` verbatim, would have shipped
  `instance:mwi_…@evil.example` to Loki at boot. A value `HostOf` cannot read is named as
  `(unreadable — see SelfUpdate:Registry)`. `OciTagListerTest` pins it against a captured logger.

## The negative control

`OciTagListerTest` pins both directions, and the negatives are what prove the guard still guards:

- **paired** — the container registry on its own host, the validator declared: the listing proceeds,
  the key presented is the one held for the *validator*, and the only host contacted is the
  container registry.
- **the same registry, nothing declared** — refused, no request leaves, no key presented. Deleting or
  loosening the host comparison turns this green, which is exactly the point.
- **a declared validator this installation holds no key for** — refused. A declaration alone is not a
  grant.
- **a target carrying userinfo** — refused, and the refusal does not echo the value.
- **a declaration that is set but unreadable** — refused as malformed, never as absent, value not
  echoed.
- **a plugin registry whose URL carries credentials** — refused naming the cause and the fix, never
  as "no registry configured"; URL not echoed.
- **the boot line** — names the host, never the configured value, and names the third validator state.

🚨 **A negative control must be proven to have RUN, not merely to be green.** Both traps were hit
while writing these, and both produced a confident pass:

- A falsification patch that **did not compile**, run with `--no-build`, reported `11/11 passed` off
  the stale assembly — "I did not check" wearing the costume of "I checked and it was fine".
- The disclosure assertion was at first **vacuous**: the fake answered an unknown host with `404`,
  and the OCI client only presents Basic *after* a `401` challenge, so no credential could ever be
  observed leaking and the assertion passed by construction. The fake now makes an unknown host
  behave like a **hostile registry** — it challenges, serves `/v2/token`, and records whatever
  secret it is handed. That is what turns "no credential was seen" from a tautology into a test.

### The vacuity audit — ask "could this assertion FAIL?", not "does it assert the right property"

A vacuous negative is a **class**, and its mechanism is now known: *a fake that refuses early can
never observe what it was built to catch.* So every negative here was audited against that one
question, by deleting the guard it protects, stripping the test to the assertion under audit, and
measuring what it reported. **Two of the five shared the shape**, and the hostile-host fixture fixed
both:

Every negative asserts the DISCLOSURE first (`CredentialsSeen`, recorded before the fake's host
check) and the message second, through `Record.ExceptionAsync` rather than `Assert.ThrowsAsync` — so
a falsification reports *what leaked*, not "no exception was thrown". Re-run in the review round of
#4094, each patch built under `-warnaserror` before it ran (a patch that did not compile is VOID, and
the runner refuses it rather than replaying the stale assembly — the first attempt was exactly that):

| negative | guard deleted ⇒ the assertion reports | vacuous? |
|---|---|---|
| no plugin registry on that host | **2 credentials** at `other.example.test` | was — an unknown host was 404'd before any credential could be observed; fixed by the hostile-host fake |
| the same registry, nothing declared | **3 credentials** | no |
| a declared validator holding no key | **3 credentials** | no |
| a target carrying userinfo | **2 credentials** at `evil.example.test` | was — the case that started the audit |
| a URL-form target (3 shapes) | the `no plugin registry` message for the raw value; under the equality form of the check, `host:443` refused with an untrue message | no |
| a declaration set but unreadable (3 shapes) | the old **"no validator is declared"** diagnosis | no |
| a plugin registry whose URL carries credentials (2 rows) | the old **"no plugin registry is configured"** diagnosis; **3 credentials** when the host guard goes | no |
| the boot line | the line with **`instance:mwi_…@evil.example.test`** in it; the two-state line saying **"NO validator declared"** | no |
| a refused key faults, never an empty list | `No exception was thrown` under a `.Catch(empty)` | no |
| a third host reached (the positives' `ReachedHosts`) | `Expected all items to match` with `third.example.test` in the list | **was** — `ServedHosts` was appended only for KNOWN hosts, so an un-credentialed request to a third host was invisible by construction; every host is now recorded before the fake decides what it serves |
| the auto-registered shape presents the durable key | **`ExchangeRequests` 1, expected 0** under `ResolveToken` | no |
| the key held for the DECLARED validator, two registries | **`mwi_issued-by-a…`** presented instead of B's under `registries.First()` | no — and with one registry configured this assertion could not exist |

The pure-function negatives (the validator-host reader, declared-vs-readable) have no fake in the
path and cannot take this shape.

**The same audit is owed across the wider suite; it is not part of this change.**

## See also

- [A Container Registry in Memex](../ContainerRegistryInMemex) — the registry, `docker_auth`, and the
  validation hook that makes one key serve both
- [Self-Update Target Selection](../SelfUpdateTargetSelection) — what the listing is then used for
- [Release & Self-Update Strategy](../ReleaseStrategy)
