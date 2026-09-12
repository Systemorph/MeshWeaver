---
Name: YAML front matter is parsed by a current library
Category: Feature
Description: The YAML reader behind every page, skill, agent and slide front matter moves up two major versions, bringing three years of upstream parser and security fixes. Every front matter in the tree was verified to parse and re-emit identically.
Icon: Checkmark
Order: -20260911
---

# YAML front matter is parsed by a current library

Every markdown node in the mesh carries a YAML front matter block — the `Name`, `Category`,
`Description` and `Icon` of a documentation page, the configuration of a skill or an agent, the
ordering and notes of a slide. One library reads all of them, and it has now moved up two major
versions, from 16.3.0 to 18.1.0.

Nothing about how you author front matter changes. That is the point, and it was measured rather
than assumed: all 1,817 front matter blocks in the platform and its plugin repository were parsed
and re-serialised by both the old and the new version, and the results were identical — the same
values, the same re-emitted bytes, and the same handling of the handful of files whose leading
`---` is a horizontal rule rather than a front matter fence.

What the upgrade brings is three years of upstream parser and security work, including a bound on
nesting depth that a hostile document can no longer use to exhaust the stack. The new ceiling is
130 levels; real front matter is two or three.

The method used to verify this — and the reasons a green build proves very little at a major
version boundary — is written up under
[Dependency Major Upgrades](/Doc/Architecture/DependencyMajorUpgrades).
