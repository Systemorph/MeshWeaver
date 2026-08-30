---
Name: The stray "User" entry in the user directory is now healed where it actually lives
Category: Fix
Description: The startup repair that removes the self-typed User declaration from the user directory now finds it inside the Auth partition, so the "As<User> … value is NodeTypeDefinition" error flood stops on stores where the earlier repair changed nothing.
Icon: Sparkle
Order: -20260830
---

# The stray "User" entry in the user directory is now healed where it actually lives

A leftover row from an early platform version made the `User` type definition look like a user account,
so the user directory kept reading it as a person and logged an error on every refresh — hundreds of
times an hour. A repair that retypes that row at startup already shipped, yet on some portals the errors
never stopped: the repair looked for the row under its own path, while on those stores the row sits in
the Auth partition, which is exactly where the user directory finds it.

The repair now also looks inside the partition each definition's own instance query is routed to, and
fixes the row there. It also reports, at every start, how many rows it read and retyped — so "nothing
found" is a number in the log, never silence.
