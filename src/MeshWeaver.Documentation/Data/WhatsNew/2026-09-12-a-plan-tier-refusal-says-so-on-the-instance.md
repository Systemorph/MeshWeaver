---
Name: A module refused by plan tier says so on the instance
Category: Fix
Description: A pre-installed package the registry refuses to an instance's plan is now named on the instance — on /health, on the package card and on the self-updater's startup line — instead of showing only the consequence.
Icon: Sparkle
Order: -20260912
---

# A module refused by plan tier says so on the instance

An instance on a plan that does not cover a package the registry declares in its default set —
a free instance and the enterprise `Hosting` package, say — used to see only the consequences:
`canPatch=False` pointing at `Modules:Assemblies`, `/health` saying "install the package from the
registry" (which the instance cannot do), and no package card at all. The refusal was logged on the
registry, where the instance cannot read it.

The registry now returns that verdict to the instance as a typed refusal — package, required tier,
instance plan — and the instance says it in one sentence wherever it used to name the consequence:

> ⛔ Not installed on this instance: **Hosting** needs plan tier **enterprise**, this instance is on **free**.

The sentence appears on `/health`'s `required_modules` line, on the package's card in the Plugin
Catalog (with no Install button, since the instance cannot install it), on the activation report,
and on the self-updater's `[SelfUpdate] starting` line when the Kubernetes patcher's package is what
was refused. The enumeration defence is unchanged: a package from a source the instance is not
granted stays indistinguishable from one that does not exist.
