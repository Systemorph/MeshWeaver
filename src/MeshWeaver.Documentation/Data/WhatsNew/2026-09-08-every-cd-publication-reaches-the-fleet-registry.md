---
Name: Every CD publication reaches the fleet registry
Category: Feature
Description: Continuous delivery now publishes every platform image and every sealed plugin-bundle publication to the fleet's own registry, cr.meshweaver.cloud, beside Azure Container Registry — the same manifest, verified by reading its digest back — so an installation that pulls from the fleet registry rolls the same build, and boots on the same sealed bundles, as one that pulls from ACR.
Icon: ArrowSyncCheckmark
Order: -20260908
---

# Every CD publication reaches the fleet registry

The fleet's own registry, [cr.meshweaver.cloud](/Doc/Architecture/ContainerRegistryInMemex), was
decided and built on 2026-09-08, and every installation except the one hosting it pulls its
images from there — but nothing had put an image there yet: continuous delivery published to
Azure Container Registry alone, and an installation pointed at the fleet registry found nothing to
roll to. This closes that last step of the program.

**Images.** Each of the three image builds (`memex-portal-ai`, `memex-migration`,
`mw-plugin-test`) copies its image to the fleet registry in the same job, right after the push to
ACR — the identical manifest, not a second build — and every consumer-visible tag the promotion
writes on ACR (the version, the commit, `main`, `latest`, the line pointers) is written on the
fleet registry in the same phase. After every copy the digest is read **back** from the fleet
registry and compared to ACR's; a difference fails that job red. A tag that exists is not evidence
about the bytes under it, so only the readback proves that an installation on either registry
rolls the same build.

**Bundles.** Every sealed publication of prebuilt NodeType bundles — the platform's own content,
the plugins, and every satellite repository's — is pushed to the fleet registry as one OCI index
per source and framework identity ([Plugin Bundles in the Registry](/Doc/Architecture/PluginBundlesInTheRegistry)),
after the storage-share seal succeeds and from the very set the shares received. The registry
gets the same "already published" rule the shares have, keyed on content and framework identity,
so an unchanged bake is not re-pushed; the immutable per-run tag and the release's identity are
recorded beside it. The share copy stays until every consumer reads the registry: the
`bundle-fetch` init container already reads the registry, the pre-warm on an installation without
it still reads the share.

**One credential, asserted before anything runs.** The registry's single publisher account is the
only thing that may push; its password is a repository secret that both continuous delivery and
the shared bake lane assert red at the start — never a step that quietly skips the push when the
secret is missing — and a refused password fails immediately rather than being retried. Every
repository that runs the bake lane provisions it, in both of GitHub's secret stores.
