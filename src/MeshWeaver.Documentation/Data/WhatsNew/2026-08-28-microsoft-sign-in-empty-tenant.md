---
Name: Microsoft sign-in no longer fails when the tenant is left unset
Category: Fix
Description: The Helm chart emitted an empty tenant id whenever an environment did not set one, and the portal read that empty string as a tenant — every Microsoft sign-in on such an environment failed with a server error. The chart now defaults to the multi-tenant `organizations` authority and the portal treats an empty tenant as unset.
Icon: PersonKey
Order: -20260828
---

# Microsoft sign-in no longer fails when the tenant is left unset

An environment variable cannot be null, only empty. The portal chart rendered
`Authentication__Microsoft__TenantId` with an empty default whenever an environment's values did not
set one, and the sign-in handler took that empty string as the tenant. The OpenID Connect authority
became `login.microsoftonline.com//v2.0`, its discovery document could never be fetched, and every
Microsoft sign-in on that environment ended in a server error — deterministically, on the first
request that touched the handler.

Two changes close it. The chart defaults the tenant to `organizations`, the multi-tenant authority
the onboarding guide prescribes; an environment that needs a single tenant still sets its id
explicitly. And the portal now treats a blank tenant as unset (falling back to `common`) instead of
building an authority out of it — the Graph sign-in helper in the platform and the Microsoft
provider in the portal package alike.
