---
Name: Images and files stop hanging after a deploy
Category: Fix
Description: Content requests that answered on one server and hung on another — leaving pages with broken images for minutes — now keep working across a deployment.
Icon: Sparkle
Order: -20260817
---

# Images and files stop hanging after a deploy

On a portal running more than one server, an image or file could load instantly on
one request and then hang for a full minute on the next — the same file, the same
page, alternating. Course and documentation pages rendered with broken images,
and the pattern came back after every update.

The two servers were talking past each other. A request always reached the server
that owned the file, but the *answer* travelled back over an internal channel whose
routing table was kept only in memory — and every deployment retires a server, which
quietly erased it. The answer was then thrown away with nothing reported, so the
request simply waited out its whole budget.

That routing table now lives in the database, so it survives a server being replaced.
Requests keep being answered across a deployment instead of only until the next one.
