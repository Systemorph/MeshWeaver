---
Name: A page that opened mid-build no longer stays empty
Category: Fix
Description: A node opened in the moment before its type had finished building used to stay blank and uneditable until the portal restarted — it now fills in by itself the instant the build lands.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# A page that opened mid-build no longer stays empty

Types you define in the mesh are built while the portal is running. Opening a page in the short
window before its type finished building was, until now, unlucky in a way that never wore off: the
page came up blank, the content refused to be edited, and nothing you did on that page brought it
back. Reloading fixed it — which is exactly why it looked like a random glitch instead of the
mechanism it was.

The mechanism: reading a node means turning what is stored into the shape the type describes. Ask
before the type exists and the honest answer is "I don't know this shape yet", so the content is
handed over as-is — raw. That part was right. What was missing is that **nobody was told when the
answer changed.** The build finished a moment later and the type became known, and the page that had
already asked was never asked again. A node does not change just because its type arrived, so no
new reading was ever triggered. The blank page stayed blank for as long as the portal ran.

**The moment a type becomes known is now an event the platform announces**, and any read still
holding raw content is redone against it. If it now resolves, the page fills in on its own — same
tab, no reload, nothing to click. Typically that is well under a second after the build lands, which
is why in practice you will simply see the page appear.

Three things deliberately did not change:

- **Content that genuinely has no type stays raw, and still says so.** The redo re-asks the same
  question and keeps the answer only when it is a real one — so a mis-typed or hand-edited row is
  not force-fitted into whatever type happened to build next. That warning in the log is still the
  right diagnosis for a broken row.
- **Nothing polls.** The page is not re-checking on a timer; it is waiting for an announcement that
  either comes or does not. A type that never builds costs nothing and changes nothing.
- **Reading is still reading.** The repaired value is delivered to whoever is looking at it; it is
  not written back over your data.

The same window also explains a family of test failures that had been re-run for weeks rather than
diagnosed. Nothing about them was random: whichever side of the race lost simply never recovered.
