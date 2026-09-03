---
Name: Notifications reach the person they are for
Category: Fix
Description: A notification is now delivered to the person it is addressed to, instead of being filed beside whatever it happened to be about. Your bell is your own, it opens far faster, and the alerts meant for platform operators — startup errors, failed reconciles, stuck instances — finally arrive in an operator's bell instead of nobody's.
Icon: Alert
Order: -20260903
---

# Notifications reach the person they are for

A notification used to be filed beside the thing it was about — a document, a thread, a plugin
record — rather than sent to the person it was for. That one decision caused three separate
problems, and they are all fixed together, because fixing any one of them alone would have made
another worse.

## Your bell is yours

Because notifications were scattered across every corner of the platform, opening the bell meant
searching all of them. On the production portal that was over four thousand records read, from two
hundred separate places, taking about ten seconds — and then almost all of them thrown away, because
they were never yours to see. It happened again every time anyone, anywhere, got a notification.

Notifications now go to your own space. The bell looks in one place, finds what is addressed to you,
and finds it immediately.

## Alerts for operators now reach an operator

The platform writes alerts for the people who run it: a boot that finished with errors, a plugin
feed that could not be read, an instance stuck showing a broken page. Those alerts were being
written correctly and filed correctly — and then never shown to anyone, because the bell's old
search deliberately skipped the one place they lived. One morning's boot reported a hundred and one
errors into a void.

They now appear in the bell of anyone who administers the platform. Only there: an alert about a
failed reconcile is an operator's problem, and it stays visible to operators.

## Some notices left your bell on purpose

The flip side of the same change. Notices about plugin updates and failed startup imports used to
appear for anyone who could see the plugin catalogue or the space in question, even though only an
administrator can act on either — and they were re-raised on every check, so they crowded out the
notifications actually meant for you. Those are now addressed to platform administrators, and your
bell shows what is yours.

Nothing about a notification's content, grouping or click-through changed: a notification still
names the document, thread or plugin it concerns, still groups by it, and still takes you there.
