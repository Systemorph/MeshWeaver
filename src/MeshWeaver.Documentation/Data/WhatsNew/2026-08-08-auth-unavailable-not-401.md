---
Name: A busy platform no longer looks like a bad API token
Category: What's New
Description: When the platform is briefly unable to check an API token, callers now get a "retry shortly" answer instead of "your token is invalid".
Icon: ShieldKeyhole
---

# A busy platform no longer looks like a bad API token

Integrations that talk to the platform with an API token — scripts, MCP clients,
CI jobs — used to be told their token was invalid whenever the platform was
having a bad moment. If the check against the token store did not come back in
time, every call answered *"Invalid or expired API token"*: exactly the same
answer a forged or revoked token gets.

That answer is the worst possible one, because it is not just wrong, it is
convincingly wrong. A well-behaved client believes it, throws the token away and
starts re-authenticating — often at the very moment the platform is least able to
cope with a flood of new sign-ins.

**"I cannot check this right now" is now a different answer from "this is not
valid".** When the token store cannot be read in time, API calls receive a
retryable *service temporarily unavailable* response, with a `Retry-After` hint
and a message that says plainly the token was **not** rejected and should be
kept. A token that is genuinely unknown, revoked, or expired still gets the
normal rejection, so nothing became more permissive: a bad token is refused as
firmly as before.

Live connections behave the same way. When a SignalR or gRPC connection cannot
have its token checked, the handshake now fails with a retryable error and the
client reconnects with the same credentials, instead of quietly connecting as an
anonymous visitor and then finding half the application missing.

In short: a hiccup now reads as a hiccup. Clients wait a moment and carry on with
the credentials they already have.
