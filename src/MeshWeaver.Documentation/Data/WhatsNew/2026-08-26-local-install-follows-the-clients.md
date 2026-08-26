---
Name: The local install follows the web clients to their new home
Category: Fix
Description: memex-local stopped part-way through every update with "ENOENT ... clients/grpc-web/package.json". The web clients had moved to the plugins repository and the local installer was still looking for them in the platform checkout; it now finds them wherever they are.
Icon: Laptop
Order: -20260826
---

# The local install follows the web clients to their new home

The web clients — grpc-web, portal-next and their siblings — left the platform repository for the
plugins repository. The local installer did not hear about it, and every `memex-local update` since
has stopped part-way through with

```
npm error enoent Could not read package.json: ENOENT: no such file or directory,
  open '.../MeshWeaver/clients/grpc-web/package.json'
```

Two more dead paths were queued behind that one, so the Next.js frontend could not be built locally
at all.

The installer now resolves the clients and the platform separately, because they genuinely live
apart: the protobuf definitions the clients generate from stay with the platform, and the clients
themselves come from whichever checkout actually carries them. Neither is assumed by name — it looks
for the file, and says plainly what to clone if it cannot find it.

One detail is worth keeping in mind whenever something moves: the old directory did not disappear.
`clients/grpc-web/` was still there afterwards, holding a `node_modules/` and a lockfile that no
move takes with it — so "does the folder exist" would have answered yes while the package was gone.
The installer, and the guard that now watches it, both probe for a file the move actually carries.
