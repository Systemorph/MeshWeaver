---
Name: A document's signatures show on its page
Category: Feature
Description: When the e-Signature package (DeepSign, Skribble) is installed on a mesh, every markdown document page renders its signature block right after its approvals — signed requests as signatures, open ones with their Sign button. Without the package nothing changes.
Icon: Signature
Order: -20260911
---

# A document's signatures show on its page

A document you sent for signature through the **e-Signature** package (DeepSign or Skribble, from
MeshWeaver.Plugins) now shows the result **on its own page**: right after the approvals section, a
**Signatures** block lists each request — signed ones as the signature (signer, signed-at in your
time zone, level, jurisdiction, provider, the signed PDF), open ones as awaiting with their
**✍️ Sign** button.

The platform delegates the block to the package exactly as it delegates approvals: it asks the
index whether the package's shared desk (`DeepSign/Workspace`) is on this mesh — a bounded probe,
never a hub call — and mounts the desk's `Signature` area with the document as its reference. On a
mesh without the package nothing is rendered and nothing is paid.
