---
Name: A deployment can host modules again
Category: Fix
Description: The setting that tells a portal where to keep the modules it installs was being discarded before it reached the portal, so an installed module never switched on. It now arrives, and features that ship as modules — the MCP server among them — work once installed.
Icon: Sparkle
Order: -20260820
---

# A deployment can host modules again

Features increasingly ship as **modules** rather than being built into the portal — the MCP server
is one of them. A module travels with a deployment, and switching it on means writing a small record
that says "this one is active". That record has to live somewhere the portal can write, which is why
a deployment names a folder for it.

The folder setting never arrived. It was written into the deployment's configuration, and it was
read by the portal, but the step in between — the packaging that turns configuration into something
the running portal can see — had no line for it. Nothing failed and nothing was logged: the setting
was simply dropped on the way, every time.

With no folder named, the portal fell back to a location inside its own installation, which it is
not allowed to write to. So the record could never be written, no module could ever be switched on,
and anything that arrives as a module was quietly absent. For the MCP server that meant its address
answered "not found" — a portal that looked healthy in every other respect and simply did not offer
the feature.

The setting now survives the trip. A deployment that names a folder gets it; one that does not gets
the same folder its other data already lives in, rather than the unwritable fallback. Installed
modules switch on as intended.
