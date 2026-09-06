---
Name: You can see what is inside a container image, without pulling it
Category: Feature
Description: Images pulled through your portal now leave a record behind — where they came from, which tag resolved to which digest, and every layer by digest and size. Answering "what is in this image?" no longer means downloading and unpacking it.
Icon: Checkmark
Order: -20260906
---

# You can see what is inside a container image, without pulling it

Until now, a container image was opaque. The only way to find out what it contained was to download
it, unpack it, and look — which meant that questions like *"which version of this did the build
actually use?"* or *"did these two builds ship the same thing?"* were answered by hand, slowly, by
whoever happened to have the tooling installed.

## What changes

Every image pulled through your portal now leaves a record behind, visible like any other content:

- **where it came from** — the registry, the repository, the reference that was asked for;
- **what a tag resolved to** — the exact content digest, computed from the bytes that were served
  rather than taken on trust;
- **what it is made of** — every layer by digest and size, the total, and for a
  multi-architecture image, which platforms it offers;
- **when it was pulled, and by whom.**

That makes a tag a *reference* instead of something to copy around. A build pipeline that needs to
pin an exact image can name the tag and read the digest, rather than pasting the digest into every
place that needs it and then keeping those copies in step.

Recording is off until an administrator names a location for it, and it can be turned off again the
same way. It never affects a pull: if recording fails, the image is still served.

## Also fixed: pulling actually works now

The pull surface announced yesterday could not complete a real `docker pull`. A client asks the
portal for a token before it will send its credentials, and the address the portal gave for that
had nothing behind it — so every client got as far as being told where to go, went there, and found
nothing. That exchange now exists, and pulling works end to end.

The token the portal hands back is your own key rather than a new credential it invents. That is
deliberate: if your key is withdrawn, pulling stops within minutes, instead of a separately issued
token staying usable until it happened to expire.

## What it does not do yet

**This is what the image's manifest says, not what is inside its layers.** It answers "which blobs,
how big, from where" — it does not yet list the *files* an image contains, which is what would
answer questions about which libraries a build compiled against. That needs reading inside the
layers themselves and is a separate piece of work.

**Your portal does not keep a copy.** Every pull still goes to the upstream registry; the portal
proxies it rather than storing it. So this makes images *visible*, not *faster*, and nothing should
depend on it as its only source.
