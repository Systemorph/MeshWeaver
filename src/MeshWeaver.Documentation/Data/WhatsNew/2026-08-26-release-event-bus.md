---
Name: How releases coordinate across repositories
Category: Feature
Description: Releases from every repository now land as facts a dependent repository can query before it builds.
Icon: Sparkle
Order: -20260826
---

# How releases coordinate across repositories

Parts of the platform ship from several repositories, and each depends on versions the others
publish. Until now that coordination was implicit. A release in any repository is now recorded as a
durable fact, and a repository that depends on it can ask what has been released rather than being
told once and having to remember.

The practical effect is that a repository waits for the versions it needs instead of building
against whatever happened to be present, and a missed notification costs a little time rather than
producing a build nobody can explain.
