---
Name: Sign in with Apple
Category: What's New
Description: The login page's Apple sign-in now works end to end — Apple's form_post callback and self-renewing client secret are handled properly.
Icon: Sparkle
---

# Sign in with Apple

The Apple button on the login page now signs you in reliably. Apple's sign-in flow differs from
other providers — it posts the response back instead of redirecting, and it requires a
cryptographically signed, short-lived client secret instead of a static one. The portal now speaks
that dialect natively and renews the secret automatically, so an operator only configures the
Services ID, Team ID, Key ID and the Sign in with Apple key. Providers stay opt-in: the button
appears only when a deployment configures Apple credentials.
