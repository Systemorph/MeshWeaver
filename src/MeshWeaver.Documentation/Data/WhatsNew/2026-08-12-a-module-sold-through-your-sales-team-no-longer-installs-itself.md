---
Name: A module sold through your sales team no longer installs itself
Category: Fix
Description: A plugin offered as "contact sales" rather than at a price was treated as free by the install machinery — any installation could pull it in unattended, and on a fresh install its pages were briefly readable by everyone, including visitors who were not signed in.
Icon: Sparkle
Order: -20260812
---

# A module sold through your sales team no longer installs itself

A plugin in the store says how it is sold. Most say it with a price. Some say it with a person: no
price at all, a sales contact instead — "talk to us, and we will set this up with you". The store
has always rendered that correctly, offering **Contact sales** where a priced plugin offers a buy
button.

The machinery that installs plugins was reading only the price. A plugin that named no price was, as
far as it was concerned, free — and free means two specific things on every installation: it may be
pulled in with nobody in the loop, and its pages are published to the world the moment it lands.
Neither is what "contact us first" means.

The effect was worst exactly where it mattered. An installation that could see the catalogue would
install such a module by itself, with no administrator ever approving it. And on a first install the
partition was opened up — cover, pages, everything — until the store's own gating caught up and shut
it again. Nothing warned anybody; the module simply behaved like something given away.

A named sales contact now counts as commercial, on equal footing with a price. Installing or
auto-updating one takes a global administrator on the receiving installation, an unattended install
refuses it and says so, and the content lands gated from the first moment — reachable only after the
conversation that the plugin asked for. Plugins that genuinely are free are untouched: nothing to
pay and nobody to ask still installs and publishes exactly as before.
