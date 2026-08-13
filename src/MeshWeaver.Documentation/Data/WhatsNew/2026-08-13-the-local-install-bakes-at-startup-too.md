---
Name: The local install bakes at startup too
Category: Feature
Description: The Homebrew local install now compiles all dynamic content at startup through the same build protocol as production — the first open after an update stops paying a cold compile — and ships the setting in a defaults layer existing installs pick up on upgrade.
Icon: Sparkle
Order: -20260813
---

# The local install bakes at startup too

Until now the local Homebrew install compiled dynamic content lazily: after every update, the
first open of each page paid a cold compile. Production stopped working that way months ago — it
bakes everything at startup — and now the local install runs the very same process, in the same
portal process: it claims the build root, bakes everything in one linked batch at full speed, and
publishes the completion signal, all observable on the build nodes exactly as in production.

Getting the setting to EXISTING installs needed its own fix: the local configuration file is
generated once (it holds your sign-in settings), so new defaults shipped inside its template never
reached anyone who had already installed. Product-following defaults now live in a tracked layer
that upgrades with the package — a `brew upgrade` plus `memex-local up` delivers them, and your
own configuration file still overrides anything, the normal way.

Two configuration keys that were silently dropped by the deployment chart — declared in values but
never templated, so they never reached any pod — are now templated, which also makes the batch
setting the production values have carried for months actually render.
