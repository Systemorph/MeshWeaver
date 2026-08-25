---
Name: The Recycle button works, and a recycle no longer breaks dependent types
Category: Fix
Description: Recycle's confirm button did nothing visible, and a package restart could leave every type in that package showing a compilation error until someone pressed Compile.
Icon: Sparkle
Order: -20260825
---

# The Recycle button works, and a recycle no longer breaks dependent types

Pressing **Recycle** on the confirmation page looked like it did nothing. The node really was
recycled, but the page that asked you to confirm was itself hosted by the node being restarted,
so the redirect it tried to send you never made it out. The whole flow now runs in the page,
which survives the restart it orders — so you get a confirmation, a wait, and one refresh.

Separately: while a package was restarting, the types inside it could not read it, and that
"could not read" was recorded as if the code had failed to compile. Every type in the package
then showed a compilation error, and nothing retried them automatically — a package restart
changes no source, and only a source change woke them up. A read that never got an answer is no
longer treated as a verdict about your code, and a type left in that state is now retried on its
own.

Also fixed: an instance showing the compilation-error page stopped watching for its type to come
back if the type was not readable at that moment, which is exactly the situation the watcher
exists for. It now keeps watching and heals itself.
