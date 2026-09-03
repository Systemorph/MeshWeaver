---
Name: A pinned platform moves in both directions — and tests have to survive it
Description: Three incidents in one day, two in tests and one a production 503, all from a satellite encoding a core contract that the platform pin can straddle. The new Architecture page says what shape survives it.
---

A satellite repo compiles against a platform commit named by `MW_PLATFORM_REF`. That pin moves
**forward** — a staleness bound now forces it — and **backward**, because freezing it is a supported
response to a bisect or an upstream outage.

Anything that encodes a core contract which changed is therefore correct on only one side of it.

On 2026-09-03 that produced three incidents from one change (anchored sign-in reads):

- two `MeshWeaver.Plugins` suites went red when the pin resolved core's tip;
- **replacing their assertions with the new literals failed in the opposite direction**, because the
  pin had meanwhile been set to a commit that predates the change;
- and image `ci.7658` paired a pre-change core with a post-change Plugins half, so every signed-in
  request got a **503**.

Both literals were wrong. That mirror-image red is the signature of the class.

The new page **Pin-Boundary Contracts** writes down the shape that survives: assert the *property*
rather than the literal, detect by reflection which platform you were built against where the
contract itself changed, prove both arms against a real checkout at the pinned sha — and, for a
runtime refusal, land it in grace mode with production serving-and-reporting while CI refuses.
