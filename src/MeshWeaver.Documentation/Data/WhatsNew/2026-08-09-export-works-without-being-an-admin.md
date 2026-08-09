---
Name: Exporting a page or deck no longer requires being an admin
Category: What's New
Description: Export to PDF, Export to DOCX and Copy node tree now run for any signed-in user — they previously failed with an access-denied error unless you happened to be an administrator.
Icon: Sparkle
---

# Exporting a page or deck no longer requires being an admin

Choosing **Export to PDF** on a page or a slide deck used to fail for most
people with a message like:

```
Export failed: Templates/Export/Pdf: Access denied:
user 'sglauser' lacks Execute permission on 'Templates/Export/Pdf'
```

The export itself was never broken. What was missing was permission to **run**
it. Exports are built as small scripts that ship with the platform, and running
one requires the Execute right on that script — but the scripts shipped without
anyone being granted that right. Administrators had it by virtue of being
administrators and so never saw the failure; everybody else was refused at the
moment they clicked the button.

Every signed-in user may now run the built-in scripts, so **Export to PDF**,
**Export to DOCX**, **Send to contacts** and **Copy node tree** all work from an
ordinary account.

This does not open anything up beyond that. The permission granted is
*read and run*, on the built-in scripts only — nobody gains the ability to add,
edit or delete a script, and it confers nothing anywhere else in the mesh.
An export still runs **as you**: it can only include content you were already
allowed to read, and it still writes its result into your own home. Signed-out
visitors are not included, since an export needs a home to write into.
