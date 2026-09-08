---
Name: The fleet has its own container registry
Category: Feature
Description: Installations can now pull their images from a registry the fleet runs itself, at cr.meshweaver.cloud, using the same instance key they already hold for the plugin registry — no separate cloud-registry credential. The deployment chart ships it as an opt-in service built from two standard images, with a publisher account for CI and pull access for every authenticated installation.
Icon: Box
Order: -20260908
---

# The fleet has its own container registry

Until now every installation pulled its container images from a cloud registry, which meant every
installation — and every CI lane that built against the platform — carried a second credential
just for that, beside the instance key it already uses for the plugin registry.

The deployment chart can now stand up the fleet's own registry, `cr.meshweaver.cloud`, as a
service beside the portal: its own pods, its own host, its own blob storage. Two standard images do
the work — the CNCF reference registry serves the images, and a small token server decides who may
do what. Nothing about it is MeshWeaver code, which is the point: a registry is a solved problem,
and a home-grown one would make every bug in it a fleet-wide pull failure.

Who may do what is deliberately simple. One publisher account, used by CI, may push, pull and
delete. Every other login presents a MeshWeaver instance key as its password; the token server
exchanges that key for a portal token — the same exchange an installation already performs, which
answers in a fraction of a second — and a valid key may pull anything. A login the portal
cannot vouch for is refused — and if the portal cannot be reached at all, the login is refused and
logged as an error rather than quietly passed.

The registry is off by default. Turning it on requires every one of its settings — host, images,
storage account, the Key Vault objects holding its certificate and secrets, the publisher's
password hash — and a missing one stops the deployment with a message naming it, so there is no
half-configured registry. The chart's own checks render a complete example on every change.

One thing stays outside: the registry's own two images are the only ones an installation still
pulls from elsewhere, because a registry cannot serve the image that boots it.

See [A Container Registry in Memex](/Doc/Architecture/ContainerRegistryInMemex) for the design and
for what was measured before this shipped.
