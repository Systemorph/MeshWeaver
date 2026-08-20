---
Name: The mobile app keeps its mesh list in your local mesh
Category: Feature
Description: The phone app connects to your local mesh as its main mesh, reads its mesh list from it, and opens into your activities.
Icon: Sparkle
Order: -20260820
---

# The mobile app keeps its mesh list in your local mesh

The phone app now treats your local mesh as its main mesh: it connects to it by default, and the
list of meshes you can switch to lives IN that mesh — as ordinary Memex Instance nodes — instead of
in device storage. Adding a portal, signing in, or removing one writes the node back, so your mesh
list survives reinstalls with your local mesh's data and is the same list every local shell sees.

On first start the local mesh sets itself up: it creates your device user and seeds the mesh list
with itself and the public memex. Opening the app now lands on your own activities, the same landing
the desktop client uses, instead of the documentation home.
