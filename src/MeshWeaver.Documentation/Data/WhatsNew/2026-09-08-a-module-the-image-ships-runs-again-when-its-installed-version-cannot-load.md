---
Name: A module the image ships runs again when its installed version cannot load
Category: Fix
Description: When a module installed from the store could no longer load on the platform an installation had moved to, the copy of that same module shipped inside the platform image was never tried — the installation ran neither, and everything the module provided disappeared. The image's copy now runs as the last fallback, the health check names it, and the module set records it.
Icon: Cube
Order: -20260908
---

# A module the image ships runs again when its installed version cannot load

Some modules exist twice on an installation: once inside the platform image, as the built-in
baseline, and once as a newer version installed from the store. The store version wins while it
works — that is what installing it means — and the platform measures, at every start, whether
the installed bytes still load on the platform now running.

When that measurement said *no*, the installation was supposed to keep the version it had. For a
store-only module it does (the previous installed version). But for a module the image also
ships, the built-in copy was never tried: the installed version had taken the built-in one's
place on the list before anything was measured, so a refusal left the module absent. That is
worse than never having installed it. On the public instance the module that renders the standard
view skins was in exactly this state after a platform update, and every skinned panel showed its
plain-text fallback instead of the view.

**The fallback now has a third step.** The order is: the newest installed version, then the
previous installed version, then the copy the image ships — each one measured the same way — and
only when none of them loads is the module absent. When the image's copy runs, the start-up log
says so (*runs the image-shipped baseline because v1.3.0 cannot load here: …*), the
`pending_module_activation` health check names it as running behind rather than as "restart
required", the installation's module-set record shows `@image` as the running version, and the
package card reads the same sentence. As soon as a build of the installed version that loads on
this platform is published, the installation adopts it, exactly as it does for a previous-version
fallback.

One shape is out of reach and is now named instead of silent: a version whose files loaded and
whose registration then failed occupies the module's name in the process, so nothing else of that
name can take over until the next start. The log says which built-in copy it could not use and
why.
