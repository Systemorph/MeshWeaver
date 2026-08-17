---
Name: Each environment declares what it carries
Category: Feature
Description: Feature flags a deployment declares for itself — carrying the packages that environment always has, re-asserted on every boot — plus packages that name the connection strings and endpoints they need, refused loudly when an environment does not supply them.
Icon: Flag
Order: -20260817
---

# Each environment declares what it carries

Two portals can run the same image and carry different content, said once in each environment's own
configuration. A **feature flag** is a named switch a deployment declares for itself — under
`Features:Flags:{name}`, beside the capability toggles that were already there — and it can carry the
packages that environment always has:

```
Features__Flags__plugins__Packages__0=Plugins/*
Features__Flags__games__Enabled=false
```

An **enabled** flag installs the packages it names; a declared-but-**disabled** one excludes them,
and the exclusion wins over every other selection. So "all of the plugin repo, without the games" is
one shared declaration plus one line in the environment that does not want them — no rebuild, and
nothing in the platform knows any package by name.

Crucially this **re-asserts on every boot**, which the existing `InstallByDefault` seed deliberately
does not: the seed runs once so an admin who later removes a package is not fought by the next
restart, which also means it can say nothing at all about a portal that is already populated. The two
knobs now sit side by side with their own meanings. Reconciling costs an up-to-date portal one
catalog listing and no writes.

Flags are read reactively — a view binds them and re-renders when configuration reloads, rather than
sampling a value that goes stale.

**Packages can also declare what they need from their environment**: a connection string, another
service's endpoint, a provisioned value. They resolve from the environment's service graph — the
same names Aspire's resource references and the deployment charts already inject — instead of each
package inventing its own configuration key. A required parameter the environment does not supply
**refuses the install loudly, naming the exact variable to set**, so nothing is ever installed
half-configured and no failure is quietly skipped.

**Settings → Administration → Composition** (platform admins) shows the whole picture for the portal
you are on: every declared flag, whether it installs or excludes, and every parameter the installed
packages need — with the variable to provision for any that is missing.
