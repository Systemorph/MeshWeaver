---
Name: An edit made while the page refreshes is no longer lost
Category: Fix
Description: A value you change in the instant the server is pushing a re-render used to be thrown away without a word — no error, no rollback, and the view then sat still. The owner now merges it, which is what it was always documented to do.
Icon: Bug
Order: -20260901
---

# An edit made while the page refreshes is no longer lost

Type into a form, and the value travels to whoever owns the data along with a note saying which
version of the page you were looking at when you typed it. That note is the point: it lets the
owner reconcile your edit with anything that happened since.

The owner was instead using it to **reject** you. It compared "the version the writer was looking
at" against "the version I am on now", and if yours was older — which it is, by definition, every
time the server has pushed anything you have not received yet — it discarded the change. Not
rejected with an error, not rolled back visibly. Dropped, in silence.

Most of the time the window is too narrow to hit. It stops being narrow the moment a view takes a
moment to redraw: while that redraw is on its way to you, **every** change you make is inside the
window. Change five values in a row on a slow-rendering form and all five could vanish — and
because the change was thrown away rather than refused, the view had nothing new to show either.
It simply stopped responding, which is very hard to tell apart from a slow connection.

Nothing about the wire format or the reconciliation rules has changed — the owner already
documented that an older base version means *merge*, and now it does. The rejection rule it was
using still applies, in full, to the direction it was written for: state the owner pushes out to
viewers, where an out-of-order frame really would corrupt what you are looking at.

Two regression tests hold the line, and both are version arithmetic rather than timing, so they
cannot quietly stop covering it on a fast machine: one asserts that a write based on an earlier
frame is applied, the other that applying it never winds the owner's version backwards — which
would have moved the same silent loss one step outwards, onto everyone else watching the same
data.
