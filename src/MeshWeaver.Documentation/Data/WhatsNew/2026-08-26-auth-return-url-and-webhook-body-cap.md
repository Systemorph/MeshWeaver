---
Name: Sign-in redirects stay on this site; webhook bodies are size-capped
Category: Fix
Description: External sign-in and logout only follow local return URLs, and the webhook inbox refuses oversized bodies instead of buffering them.
Icon: ShieldCheckmark
Order: -20260826
---

# Sign-in redirects stay on this site; webhook bodies are size-capped

After signing in through an external provider — or signing out — the portal now only follows a return URL that points back to a page on this site. A link that tried to send you somewhere else after login is ignored and you land on the home page instead.

Separately, the webhook inbox now enforces its body-size limit for every request, including ones that do not declare a length up front, and answers *413 Payload Too Large* rather than reading an unbounded body.
