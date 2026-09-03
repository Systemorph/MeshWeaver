---
Name: An update reminder is told once, not every hour
Category: Fix
Description: A plugin with an update waiting added a fresh "Update available" notification to your bell every time your installation checked — several a day, per package, for as long as you did not act. You now get one reminder per version, it stays dismissed once you dismiss it, and a genuinely newer build still gets its own.
Icon: Alert
Order: -20260903
---

# An update reminder is told once, not every hour

When a plugin you have installed publishes a new build, your installation tells you so with an
**Update available** notification on the bell, and leaves the decision to you. That is the intended
behaviour: nothing installs itself unless you asked it to.

What was not intended is that it told you again on every check. Your installation re-examines what
the plugin catalogue is serving whenever it starts, whenever it reads a catalogue, and whenever a
plugin repository publishes — and each of those checks added a *new* notification, because the
reminder had no memory of having been given. A package you had simply not got round to updating
produced several identical rows a day, for as long as you left it. On our own portal, **124 of the
newest 200 notifications** were this one message about two packages.

The effect was worse than clutter. Those rows are what the bell has to read every time you open it,
so a reminder you had already seen made the bell slower for everyone who could see it — and a
notification you had dismissed came straight back.

**A reminder is now a statement about a version, not an event.** The first time a new build of a
package is available, you get one notification. Every later check that still sees the same build
says nothing at all: no new row, no re-lit bell, and nothing fetched. If you dismiss it, it stays
dismissed.

When the plugin publishes a genuinely newer build, that is a different thing to be told about, and
you get a fresh reminder for it — which is the whole point of the message.
