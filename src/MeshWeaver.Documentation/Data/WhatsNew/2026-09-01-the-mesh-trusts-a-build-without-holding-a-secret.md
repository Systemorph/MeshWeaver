---
Name: The mesh trusts a build without holding a secret
Category: Feature
Description: A repository's CI can now authenticate to the registry with the OIDC token GitHub already mints for the run, checked against a Build Principal node an admin can list and revoke — no key stored anywhere, and no rule left in a cloud tenant nobody can query.
Icon: ShieldCheckmark
Order: -20260901
---

# The mesh trusts a build without holding a secret

Until now, a build that needed something from the registry had to prove itself with a credential
somebody had provisioned: an identity in a cloud tenant, with a matching rule alongside it. The
result was a security fact nobody in the mesh could see. Which repositories were trusted, on which
events, by whose decision — the only way to find out was to have access to the tenant, and the only
way to discover that a rule covered the wrong event was to run a build and read the error.

**A GitHub Actions run already carries a passkey for services.** It can ask GitHub for a short-lived,
signed token describing itself — which repository, which event, which branch. Verifying that token is
all a federated credential ever did. So the mesh does it itself, and the rule that decides what the
build may do now lives on a node:

```
search nodeType:BuildPrincipal
```

That is the complete list of repositories this deployment trusts and exactly what each may do —
readable, auditable, and revocable the way every other record is. Nothing is minted, rotated, pasted
into a repository's secrets, or able to leak, **because there is no key**.

A principal records the repository, which events may act and with which verbs, and the sources it may
fetch from or publish to. The scope split is the security tie: the identity that publishes a source is
the identity that may fetch what it depends on, and it can do neither outside its scopes. A global
admin creates one; writing `requestedAction: Revoke` ends it, and the stop takes effect on the very
next request rather than when something notices.

**A verified signature is deliberately not enough.** Every workflow run on GitHub carries a token
signed by the same keys, so the signature says only *which* repository asked; a repository with no
node is refused like any stranger, and the repository is matched exactly — never as a prefix. The
audience is mandatory, so a deployment that has not configured one has no build-principal surface at
all rather than one that accepts anything. And when GitHub's signing keys cannot be read, the answer
is **"try again", not "your identity is unknown"** — an unreachable check is a third state, and a
build sent hunting for a credential that was never the problem is a wasted afternoon.

Build principals are admitted on the sealed-publication routes only. A build is not an installation:
it holds no licence and no plan, so every other route refuses it exactly as before.
