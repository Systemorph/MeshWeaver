---
Name: An exported document no longer comes out blank
Category: Fix
Description: Some documents exported to PDF, Word or email as a cover page and a contents list with nothing after it. The export now reads every way a document body can be stored, and says so when it genuinely cannot.
Icon: Sparkle
Order: -20260813
---

# An exported document no longer comes out blank

Exporting a document could produce a file with a cover page, a table of contents, and then nothing
at all. The pages were there, the headings were listed, and the body was simply gone. Nothing
failed, no error appeared, and the download completed normally — so it read like an empty document
rather than a broken export.

Which documents it hit was not obvious from looking at them. On screen they were perfectly normal:
the text was visible, editable and searchable. The difference was invisible — how the body happened
to be *stored*. A document written by an import, created through the API, or saved before its
content format existed keeps its text in a slightly plainer form than one typed into the editor.
The export recognised only the editor's form, found nothing where it looked, and carried on.

Two things made this worse than a normal bug. Including child pages made losses silent rather than
visible: a chapter with no text is skipped entirely, so a whole section could vanish from a report
without leaving so much as a blank page behind. And the same reading step existed in three separate
copies — one for PDF, one for Word, one for email — so all three formats were affected, and fixing
one would not have fixed the others.

There is now a single reading step, shared by all three formats, that understands every form a
document body is stored in. Documents that used to export blank now export their text. And in the
one case where a body genuinely cannot be read, the export writes that into its activity log naming
the page, instead of quietly handing back an empty chapter and leaving you to wonder what you did
wrong.
