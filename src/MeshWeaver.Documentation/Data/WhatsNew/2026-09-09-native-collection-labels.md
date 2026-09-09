---
Name: Collection labels retain their readable text
Category: Fix
Description: Labels render native collections consistently with values received over the mesh.
Icon: Info
Order: -20260909
---

Labels bound to arrays, lists, or dictionaries retain their readable text before transport serialization. This restores collection rendering while preserving safe fallback handling for values that cannot be converted to numbers.
