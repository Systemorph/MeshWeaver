---
Name: The plugin catalog stops re-reading its repository on every request
Category: Fix
Description: A registry read the whole source repository from GitHub each time anyone opened the catalog, so a catalog open could sit for half a minute before anything was even retried. It now reads once per change, and every reader is still shown only what they are licensed for.
Icon: Checkmark
Order: -20260913
---

# The plugin catalog stops re-reading its repository on every request

Opening the plugin catalog — or any page that counts what is available — asks a registry for its
list. Answering that question used to mean fetching the entire source repository from GitHub and
re-reading every module description in it, **from scratch, for each request**.

Measured from inside two production installations: answering with an 8.7 KB list connected in three
hundredths of a second and then took **twelve to nineteen seconds** to send the first byte. Measured
from an installation asking: about **sixty requests a day** ran past the half-minute a single attempt
is allowed, over more than a month.

That never showed as an outage — a later attempt usually succeeded — which is why it went unnoticed
for so long. What it did show as was a catalog that could sit spinning for half a minute, and sixty
red lines a day that a reader had to recognise and dismiss.

## What changes

**The repository is read once per change, not once per request.** The list a registry builds from a
repository is now kept and reused. Requests that arrive at the same moment share a single read
instead of each starting their own.

**A merge is still visible immediately.** A registry already hears from its repository whenever a
build goes green; hearing that is what makes it read again. The reuse has a five-minute ceiling as
well, so a missed notification costs at most a few minutes of staleness rather than an indefinite
wait — that ceiling is the backstop, not the mechanism.

**Everyone still sees exactly what they are licensed for.** What is reused is what the REPOSITORY
said, before any decision about who is asking. Who may see which module — and which of those a plan
covers — is still decided for every request, individually. Reusing the finished answer instead would
have been a way for one installation to be shown another's catalog, and that is deliberately not what
happens here.

**Downloads are unaffected.** Installing a module still reads its files fresh every time. Only the
LIST is reused.

## What this does not change

**A failure is never kept.** If a read fails, nothing is remembered from it and the next request
tries again — a momentary failure cannot become a permanently broken catalog.

**It can be switched off.** Setting `PluginCatalog:ListingCacheSeconds` to `0` restores the previous
behaviour exactly. A malformed value falls back to the default rather than to "off", so a typo cannot
silently bring the old cost back.

**Nothing about what a registry serves changes.** The same catalog, the same entitlements, the same
wire format — read from a repository that is no longer asked the same question sixty times an hour.
