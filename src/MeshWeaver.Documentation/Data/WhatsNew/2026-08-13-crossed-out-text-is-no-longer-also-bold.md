---
Name: Crossed-out text in an export is no longer also bold
Category: Fix
Description: Text you struck through came out bold as well in PDF and Word — as did highlighted and inserted text, while subscript came out struck. Each now prints as what it is.
Icon: Sparkle
Order: -20260813
---

# Crossed-out text in an export is no longer also bold

Striking a word out — writing `~~like this~~` — is how you show that something has been dropped or
superseded. Exported to PDF or to Word, that word came out **bold** as well as crossed out. It drew
the eye instead of retiring it, which is the opposite of what the author meant, and it was wrong in
both formats because both are drawn from the same reading of the document.

The reason was a small confusion about what makes text bold. Every kind of emphasis — bold, italic,
crossed out, highlighted — is written by wrapping the text in a symbol, and bold uses a *doubled*
one (`**bold**`). The export decided a passage was bold whenever its symbol was doubled, which is
true for `**` and also true for `~~`. So crossing something out made it bold as a side effect.

The same slip affected three more kinds of emphasis nobody had noticed:

- `==highlighted==` and `++inserted++` came out bold, for the same doubled-symbol reason.
- `~subscript~` came out crossed out, because it shares a symbol with strike-through.
- `^superscript^` came out italic.

An export now decides emphasis from *which* symbol was used, not how many of them there are.
Crossed-out text is crossed out. Bold is still bold, italic still italic, and bold with something
struck out inside it keeps both. Highlighting, insertion marks, subscript and superscript have no
counterpart in an exported document, so they now print as ordinary text — their words are all still
there, they simply no longer arrive wearing an emphasis you did not write.
