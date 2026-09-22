---
Name: The Redirect-Target Contract — Both Ends of a returnUrl
Category: Architecture
Description: Every returnUrl sink validates local-only, so every returnUrl source must mint local — the second half of a rule that only had a guard on one end, and the silent failure mode that costs a whole sign-in flow.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5v0a5.5 5.5 0 0 1-5.5 5.5H11"/></svg>
---

# The Redirect-Target Contract

A `?returnUrl=` (also spelled `returnPath`, `returnTo`) carries "where the user was going" across a
flow that has to interrupt them — a sign-in, an onboarding form, a consent dialog. The portal has one
rule for it, and it is short:

> **A redirect target is honoured only if it is LOCAL — a path on this host. Anything else falls back
> to the root.**

`ReturnUrlPolicy.Sanitize` is that rule for the portal's server surface; the GUI has one
implementation of the same rule for its client surface. Both are correct, both are tested, and
neither is negotiable — an unvalidated redirect target is an open redirect, which is how a link that
looks like the tail of our own login flow sends a freshly-signed-in user somewhere else.

## The contract has two ends, and only one of them had a guard

The rule above is stated as a property of the **sink**: the thing that consumes an incoming target
and redirects to it. `RedirectSinksUseOnePolicyGuard` enforces exactly that, and the invariant it
pins is not "the policy is right" but "there is only one policy, and every sink uses it" — because
the defect it was written for was three hand-written copies of the rule, two of them subtly weaker
than the shared one.

What nothing enforced is the **source**: the thing that MINTS a target and hands it onward. And the
two ends are not symmetric in what they cost when wrong:

| | Wrong sink | Wrong source |
|---|---|---|
| What happens | a non-local target is followed | a non-local target is **refused** |
| Who is harmed | the user, by an open redirect | the flow, silently |
| How it surfaces | a security finding | "the feature intermittently does not work" |

A wrong source produces no error anywhere. The sink refuses the target — doing its job — and the
flow simply carries on without it. The GUI's helper for a target it will not keep returns *nothing*
rather than substituting a destination, precisely so that a refused target does not become a
redirect to a place the user did not ask for. So the parameter is omitted, the flow completes, and
the user lands on `/`.

## What that looked like in production

`/authorize` (the OAuth authorization endpoint MCP clients use) minted its return target as an
absolute URL — scheme, host, path and query — and redirected to `/login` with it. Every sink
downstream then refused it, correctly, because an absolute URL is not local. The consequence, end to
end:

1. An MCP client sends the user to `/authorize?…`.
2. The user is not signed in, so `/authorize` redirects to `/login?returnUrl=<absolute url>`.
3. The login page will not keep a non-local target, so the provider link carries **no** `returnUrl`.
4. The user signs in with Microsoft — successfully — and lands on `/`.
5. The `/authorize` request is gone. **No authorization code is ever issued.**
6. The client waits for an "authentication successful" that cannot arrive.

Nothing in that sequence logs an error. From the user's side a sign-in that *worked* did not connect
anything, and from the client's side the credential looks expired — which is how it was reported
(#5074: *"Re-authenticating no longer helps"*).

The absolute form also bought nothing: `/authorize` and `/login` are the same origin, so the scheme
and host were pure cost. `OnboardingMiddleware`, two files away, carries the same request the same
way and gets it right — `{Request.Path}{Request.QueryString}`, with a comment saying why that is
inherently local. One deviation, no guard to catch it.

## The rule, for anyone minting one

**Mint `{Request.Path}{Request.QueryString}`. Never `{Request.Scheme}://{Request.Host}…`.** Then pass
it through the shared policy anyway, so the intent is legible at the call site and the value is one
the policy demonstrably keeps.

`RedirectSourcesMintLocalTargetsGuard` (`test/Memex.Portal.Shared.Test`) enforces the source end
across `memex/Memex.Portal.Shared` and `src/`. For each minting site it resolves the interpolated
value to its assignment in the same file:

- an assignment composing a scheme and host → **refused**, naming the file and the expression;
- an inbound parameter of the enclosing method → a **forward**, not a mint; that value's safety is
  the sink guard's subject where it lands;
- anything else → **fails closed**, because a site the guard cannot classify is not a clean one.

It also asserts it found at least one minting site. A guard whose subject is renamed or moved out
from under it otherwise passes having checked nothing, and that failure is indistinguishable from
success.

> 🚨 **A false positive here is worth reading, not suppressing.** On its first run the guard reported
> a site in each connect endpoint — both of which DOCUMENT their route as
> `GET /connect/github?returnPath={path}` in an XML comment, where the brace is a placeholder for a
> human reader and names no value at all. The fail-closed branch was right to report something it
> could not resolve; the fix was to make prose not a subject, not to loosen the classification.

## Source references

| File | Purpose |
|---|---|
| `memex/Memex.Portal.Shared/Authentication/ReturnUrlPolicy.cs` | The one rule for the portal's server surface |
| `memex/Memex.Portal.Shared/Authentication/OAuthConnectController.cs` | `/authorize` — mints the login return target |
| `memex/Memex.Portal.Shared/Authentication/OnboardingMiddleware.cs` | The same mint, done right, with the reason written down |
| `test/Memex.Portal.Shared.Test/RedirectSinksUseOnePolicyGuard.cs` | The sink end: every consumer routes through the one policy |
| `test/Memex.Portal.Shared.Test/RedirectSourcesMintLocalTargetsGuard.cs` | The source end: every minted target is one the policy keeps |
| `test/Memex.Portal.Shared.Test/OAuthAuthorizeReturnUrlIsLocalTest.cs` | `/authorize`'s own target, with a control on each side of the change |
