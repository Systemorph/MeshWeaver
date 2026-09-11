---
Name: A module update during a rollout no longer blocks it
Category: Fix
Description: When a module's content changed while a new portal version was starting up, the start-up check could build a type from its old definition and its new files, report a failure that did not exist, and hold the rollout. It now builds each type as it stands when it is built.
Icon: Checkmark
Order: -20260911
---

# A module update during a rollout no longer blocks it

A new portal version checks every type before it starts serving. When a module update arrived
while that check was running, the check could combine a type's previous definition with its
updated files. The result failed to build, the check reported the type as broken, and the rollout
waited until the new version was restarted.

The check now reads each type again immediately before building it. When the definition or its
files changed since the check started, it builds the definition as it now stands, with the files that
definition names, and when it cannot tell which files those are it reports the type as not checked
instead of broken. A type that is genuinely broken still stops the rollout.
