---
Name: A gRPC test no longer loses its port to another process
Category: Fix
Description: A test that picked a free port and released it before use could red an unrelated pull request when anything else on the machine took that port first.
Icon: Sparkle
Order: -20260826
---

# A gRPC test no longer loses its port to another process

The trusted-endpoint test needed two free network ports before it could start its server, and it
found them the usual way: open a socket, ask the operating system which port it assigned, then close
it again. Between that close and the server's own bind, the port belonged to nobody — so anything
else on the machine could take it, and the server then refused to start with *address already in
use*. On a quiet machine the gap is invisible; on a loaded build agent it is wide enough to catch,
which is how the failure kept appearing on pull requests that had not touched the code at all.

The test now keeps the socket it opened and hands that same socket to the server, so the port is
owned continuously and there is no moment for anything else to claim it. A new check runs the losing
interleaving on purpose — a rival process grabbing the port at exactly the wrong instant — and fails
if the port is ever left unowned again.
