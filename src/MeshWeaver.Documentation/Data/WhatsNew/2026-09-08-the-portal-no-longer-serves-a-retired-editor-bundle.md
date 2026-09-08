---
Name: The portal no longer serves a retired editor bundle
Category: Fix
Description: The portal went on publishing an old, unused code-editor bundle — 366 files, including a 3.6 MB script carrying a sanitiser with published security advisories — long after it stopped loading any of them. They are gone from what the portal ships, and the page every visitor downloads first is now half the size.
Icon: ShieldCheckmark
Order: -20260908
---

# The portal no longer serves a retired editor bundle

The portal's code editor used to come from a prebuilt bundle inside one of its packages. That bundle
was retired earlier: the portal now builds its own editor with an up-to-date HTML sanitiser, and no
page has loaded the old one since.

**Retiring the bundle stopped it being loaded. It did not stop it being served.** The package is
still needed for one small file that connects the editor to the rest of the portal, and a package
publishes everything it ships or nothing — so all 366 of the old bundle's files stayed on the
portal, downloadable by anyone who knew the address. Among them was the 3.6 MB script carrying the
sanitiser version the security scan had flagged, and the loader whose use of `eval` the same scan
names.

Nothing loaded them, so this was never a live vulnerability: making a visitor's browser fetch that
file would already require the ability to run scripts on the page, which is the thing an attacker
does not have. It was inventory — a file with known advisories, published under the portal's own
name, waiting to be found by the next scanner, crawler or third-party asset inventory that looked.

**The portal now publishes exactly the one file it actually needs.** Measured on the two portal
builds either side of the change: 366 files under the package's address became 3 (the one file plus
its two compressed forms), and the 726 published addresses under the retired tree became none. The
old addresses now answer *not found*.

Two things visitors get for free. The page every visitor downloads first carries a list of the
portal's script addresses, and it shrank from 96 KB to 44 KB — the list itself from 81 KB to 29 KB,
because 240 of its entries named files nothing had used for days. And the portal image is smaller by
everything the old bundle weighed.

The editor is unaffected: it was already being built and served separately, and every file it loads
was verified present on the new build.

See [OWASP ZAP Scan — Every Release](/Doc/Architecture/SecurityScanning) for the measurement and the
method behind it.
