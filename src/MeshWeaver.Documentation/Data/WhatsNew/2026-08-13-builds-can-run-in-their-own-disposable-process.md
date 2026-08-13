---
Name: Builds can run in their own disposable process
Category: Feature
Description: The portal binary gains a bake mode — an ephemeral build master with its own cluster identity that compiles everything, publishes the GO, and exits, so serving pods start against a full cache and build memory is reclaimed by process exit.
Icon: Sparkle
Order: -20260813
---

# Builds can run in their own disposable process

The same portal binary — and therefore the same image and the same framework fingerprint — can now
run as a dedicated build process instead of a serving portal. In bake mode it joins no serving
cluster (it gets its own cluster identity, so build work and live traffic cannot contend or starve
each other), runs the coordinated build against the shared stores, publishes the GO signal, and
exits.

Exiting is the point: everything a full build allocates — measured at roughly two gigabytes for a
complete sweep — is reclaimed the moment the process ends, instead of living on inside a serving
pod. And because a build's outcome must never be ambiguous, the process's exit code is its
verdict: success, a faulted sweep that verified nothing, or a misconfiguration that ran no build
at all.

Serving pods keep their own build capability as a fallback — the claim on the build root
arbitrates cleanly between a dedicated build process and a pod baking for itself, so running the
build ahead of a rollout is an acceleration, never a requirement.
