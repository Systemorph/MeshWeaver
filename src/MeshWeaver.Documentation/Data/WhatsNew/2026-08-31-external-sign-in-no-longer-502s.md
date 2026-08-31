---
Name: Signing in with Microsoft or Google no longer fails with 502
Category: Fix
Description: Fixed a self-hosted deployment returning 502 Bad Gateway at the end of an external sign-in, after the identity provider had already accepted the password.
Icon: Sparkle
Order: -20260831
---

# Signing in with Microsoft or Google no longer fails with 502

On a self-hosted portal, signing in with an external provider could end in a bare
**502 Bad Gateway** page — after the provider had accepted your password and sent you back.
Nothing in the portal's own logs looked wrong, because nothing in the portal was: the sign-in
was completed correctly, and the answer was discarded on the way back to your browser.

The proxy in front of the portal reserves a fixed amount of room for a response's headers, and
its default is 4 KB. The last step of a sign-in writes your session cookie, which carries your
identity's full set of claims — for a work or school account with group memberships, comfortably
more than that. The proxy dropped the response it could not fit and answered 502 instead.

The deployment chart now reserves enough room for it. Developer login was never affected, since
it writes a much smaller cookie — which is why a deployment could look perfectly healthy while
every external sign-in was broken.

**If you self-host:** upgrade the chart, or add
`nginx.ingress.kubernetes.io/proxy-buffer-size: "32k"` to your portal ingress annotations.
