---
Name: A local install consumes the cloud registry — and Homebrew delivers it
Category: Feature
Description: memex-local gains a registry mode — the local portal becomes a consumer of memex.meshweaver.cloud, running the CI-built image and landing the compiled modules a source checkout could never land — and the Homebrew tap is now published by CI, so `brew upgrade memex-local` follows main.
Icon: Sparkle
Order: -20260830
---

# A local install consumes the cloud registry — and Homebrew delivers it

A local memex on Colima used to be its own plugin registry, serving the developer's `MeshWeaver.Plugins`
checkout. A checkout holds source, not assemblies, so every module-declaring package installed its
content and silently skipped its binary: no Radzen charts, no Analysis or Entity views, no maps, no
speech — twenty-six of twenty-eight module packages recorded as installed with no DLL anywhere, and
the five the image *requires* had to be blanked so the portal would report ready at all.

`memex-local registry https://memex.meshweaver.cloud --key mwr_…` turns that install into what every
cloud instance already is: a **consumer** of the registry. It pulls the CI-built multi-arch image
(the native arm64 member — no checkout, no .NET SDK), registers itself on first boot with a key a
platform admin minted, installs the packages it is granted and lands their compiled modules from the
registry's bundles. The same `memex-local update` then rolls it forward. The self-registry mode stays
one command away (`registry off`) for developing plugins from a checkout, and the setting that
blanked the required modules now lives only in that mode's layer, so a registry-mode install reports
its modules honestly.

Homebrew now delivers the CLI instead of describing how it might: the tap `systemorph/memex` is
published by the platform's own CI — every pull request that touches the CLI or the chart it vendors
installs and tests the formula on a macOS runner, under macOS's own bash, and every merge to main
releases a new version to the tap. `brew upgrade memex-local` follows main.
