---
nodeType: WhatsNew
Name: A package whose module never lands now says so
Category: Fix
Description: An installed package that declares a compiled module whose assembly is not on this installation is named once at boot, instead of reporting a clean install while half of it is missing.
Icon: Sparkle
Order: -20260910
---

# A package whose module never lands now says so

A package that declares a compiled module arrives in two halves: its nodes come with the package, and its layout areas and node types come only with the module. Installing the first half and not the second lost the second in silence. Every surface read clean — the package was installed, the install record named the module, the boot reconcile reported it up to date, and its guide page rendered. Only the areas were absent, and nothing anywhere said why. On a course that is the certificate: a learner could finish, receive a correct certificate, and find no way to export it.

Boot now names those packages once, with the module each one declares and what is missing because of it. The check asks the boot loader's own question — the same resolution the module loader uses, then the activation record — so a bundle that has landed and is only waiting for a restart is not reported as absent. It is a warning and never a refusal: a portal that will not start cannot be given the module it is missing.
