---
Name: Git updates restore package README pages
Category: Fix
Description: Updating a package through Git now restores its declared README page instead of skipping it or deleting an existing copy.
Icon: ArrowSync
Order: -20260909
---

Git updates now recognize README pages declared by a package. Missing pages are restored, and pages still present in the package survive a full import. A generated repository landing page remains separate from the mesh's content, and exporting an authored README no longer adds a second generated copy.
