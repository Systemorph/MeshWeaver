---
Name: A closed page gives its memory back
Category: Fix
Description: Closing a page or ending a session used to leave part of it behind in the server's memory. Those leftovers are now released, so a portal that has been running for days stays as responsive as one that just started.
Icon: TopSpeed
Order: -20260906
---

# A closed page gives its memory back

Every open view — a document, a dashboard, a chat thread — is backed by a small piece of machinery on
the server that keeps it in sync with the data behind it. When you close the page, or your session
ends, or the portal recycles a node, that machinery is shut down.

**Shut down, but until now not entirely let go.** Something still held a reference to it, so the
memory it used could never be reclaimed. On a busy portal that added up: a server running for a
couple of days accumulated thousands of these leftovers, and the garbage collector could do nothing
about them because, as far as it could tell, they were still in use. The symptom people actually
noticed was the portal getting steadily slower the longer it had been up — long pauses, then a
restart that made everything feel fast again for a day.

The leftovers are now released, from both directions: when a view is closed normally, and when the
session behind it is torn down without the view being told. A portal that has been up for days now
behaves like one that started an hour ago, and the periodic slow-down-then-restart cycle goes with
it.

Nothing about how views work has changed — this is purely what happens after they are finished with.
