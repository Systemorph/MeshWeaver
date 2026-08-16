---
Name: Your threads are listed again
Category: Fix
Description: The side panel's thread picker and the in-thread navigation menu list your threads instead of coming up empty.
Icon: Chat
Order: -20260816
---

# Your threads are listed again

Opening the thread picker in the chat side panel showed "No threads yet" on almost every page, and
the in-thread navigation menu's list of your other open threads was empty for everyone.

Two separate causes, one symptom. The picker only looked for threads belonging to the page you
happened to be viewing, so unless you had started a thread on that exact page there was nothing to
find. The navigation menu asked for threads by their author, but a thread records its author in the
conversation itself rather than on the node, so the filter never matched anything.

Both lists now show your threads wherever you started them — the picker lists all of them, newest
first, and the navigation menu lists the ones you have not marked done.
