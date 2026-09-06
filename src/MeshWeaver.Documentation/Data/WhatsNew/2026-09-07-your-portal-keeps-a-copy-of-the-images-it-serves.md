---
Name: Your portal keeps a copy of the images it has already served
Category: Feature
Description: Container images pulled through your portal are now kept, so the next pull of the same image is served locally instead of fetched again — and when the upstream registry is unreachable, the portal says so rather than pretending the image is gone.
Icon: Checkmark
Order: -20260907
---

# Your portal keeps a copy of the images it has already served

Your portal could already serve container images, but it fetched every one from the upstream
registry every time — it passed bytes along without keeping any. Pulling the same image twice cost
the same download twice, and if the upstream registry was unavailable, so was every image.

## What changes

**The second pull is served from your portal.** An image pulled through it is kept, and the next
request for the same content is answered locally without contacting the upstream registry at all.

**A kept copy is checked, not assumed.** Container content is named by a fingerprint of its own
bytes, and the portal verifies that fingerprint before keeping anything. Content that does not
match what was asked for is passed on to whoever asked, and thrown away rather than kept — so
anything the portal has kept is provably the right bytes, which is what makes serving it during an
outage safe rather than hopeful.

**Nothing kept can go stale.** Only content addressed by its fingerprint is kept. A *tag* — a
moving name like `latest` — is deliberately never kept, because a tag can be pointed at something
new tomorrow. There is nothing to expire and nothing to refresh: what is kept is either exactly
right or not there.

## The one that matters most: "I could not reach it" is not "it does not exist"

If the upstream registry cannot be reached, the portal says **exactly that**, distinctly from
saying the image is not there. Those are two very different pieces of news — one means wait and
retry, the other means the image is genuinely gone — and a tool that confuses them sends people
looking for the wrong problem. The portal keeps four answers separate: served from its own copy,
fetched fresh, genuinely absent, and could-not-reach-the-registry.

## What it does not do

**It is a cache, not an archive, and it is important not to read it as one.** It has a size budget
and discards the least recently used content when it runs out, so something kept today may not be
kept tomorrow. Its promise runs one way only: having a copy avoids the fetch, and not having one
falls back to fetching exactly as before. **It can never make images less available — but it is not
a backup, and it is not protection against something being deleted upstream.** Refusing to discard
content that a running deployment depends on is separate work, still to come.

**A pull by moving name still needs the upstream.** Resolving `something:latest` to the exact
content it currently points at can only be answered by the registry that owns the name. A pull that
names exact content can be served entirely from the portal's own copy; a pull that names a tag
cannot.

**Keeping copies is off until an administrator turns it on** by naming a folder and a size budget,
and turning it off again returns the portal to passing every pull straight through. There is
nothing to migrate in either direction.
