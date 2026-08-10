---
Name: Code cells no longer forget earlier cells
Category: Fix
Description: Running several code cells in quick succession could fail with "the name does not exist" — a later cell could slip ahead of the one that defined the variable while the page's kernel was still waking up.
Icon: Sparkle
Order: -20260810
---

# Code cells no longer forget earlier cells

Running a page with several code cells — or pressing Run on a few cells back to back — could
occasionally fail with an error like *"the name 'sharedValue' does not exist"*, even though an
earlier cell on the same page had just defined it. Re-running the page usually worked, which made
the failure look random.

The cells were never lost and the kernel never forgot anything: the cells simply did not always
arrive in the order they were sent. While the part of the portal that runs a page's code is still
waking up, its incoming messages wait in a short queue so they can be handed over in order. The
moment it finished waking up, however, a newly arriving message was handed straight to it — jumping
ahead of messages still waiting in that queue. A cell that *uses* a variable could therefore run
before the cell that *defines* it, and the kernel rightly reported the name as unknown.

Now a newly arriving message joins the back of the queue whenever the queue is still emptying, so
everything sent to the same destination is processed strictly in the order it was sent. Once the
queue has drained, messages are handed over directly as before — nothing gets slower, the order is
just guaranteed. Pages with many cells run reliably on the first try, and the same fix applies to
anything else that sends several updates to a part of the portal that is just starting up.
