---
Name: Actions that run when you sign in
Category: Feature
Description: The platform can now bring your home up to date the next time you sign in — and app icons stop showing the generic placeholder.
Icon: Sparkle
Order: -20260824
---

# Actions that run when you sign in

Until now, anything the platform wanted to change for people who already had an account had to be
done by a database migration, which meant it could not be done at all without shipping a new
version. Administrators can now declare an action that runs for each person the next time they sign
in — either once, ever, or on every sign-in — and use it to bring existing accounts in line with new
ones. A typical use is refreshing what is pinned to everyone's home page.

Anything an action changes belongs to you afterwards. A once-only action never runs a second time,
so if you re-arrange your pinned items later, they stay exactly as you left them.

The first thing riding on this: the icons on your Apps grid. Apps that were showing the generic
puzzle-piece placeholder now pick up their real icon, so you can recognise an app before reading its
label — including apps you install from the Store later on.
