---
Name: The header names the build serving you
Category: Feature
Description: A chip in the portal header shows which build and which instance is serving your session, flags a newer one, and refreshes you onto it.
Icon: ArrowCircleUp
Order: -20260817
---

# The header names the build serving you

The portal header now carries a small chip with the build this session is being served by. Hover it
and it also names the *instance* — under Kubernetes, the pod. When a newer build exists the chip
switches to it, turns into an arrow, and clicking it reloads you onto whatever instance is serving
now.

The reason it is always on screen, rather than appearing only when an update is waiting, is that
half the question is "did I arrive?". An indicator that shows up to announce an update can tell you
one is available but never that you are now on it. Because the running build is on screen before and
after, a refresh that lands you on the new instance is visible as the value changing — which is what
you want to confirm before starting something long-running, like an agent thread round.

Two states read differently on purpose:

- **An update is available** — the chip names the new build and the click refreshes you onto it.
- **An update is held** — a package this deployment runs has no artifact for that build yet, so the
  platform is deliberately not moving. The chip says so and offers no refresh, because refreshing
  cannot clear a hold. The full reason is on the Updates tab.

Clicking the chip at any other time opens the About page, which carries the rest of the build
identity: the commit, the runtime, and the plugins installed on it.

The chip is shown to signed-in users only — the build and instance names are deployment detail, not
something a public page hands to every visitor.
