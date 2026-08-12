---
Name: Share a document as an email, in your own name
Category: Feature
Description: Share ⇒ as email sends the document as the message itself, from your own mailbox, with embedded views rendered into it.
Icon: Mail
Order: -20260811
---

# Share a document as an email, in your own name

Sending a document used to mean sending a PDF. The node menu's send entry exported the
page to a file, attached it, and put a one-line note in the message — so the recipient
got an attachment to open rather than something to read, and the copy-and-paste
alternative lost the parts of the page that matter most.

**Share ⇒ as email** now puts the document *in* the message. Open it from the node menu,
next to Export to PDF and Export to DOCX, pick a contact or type an address, and the page
arrives as the email itself. The PDF attachment is still there as an option when a file is
what you actually want.

## It comes from you

Sharing is a personal act, so the message goes out **from your own mailbox**, using the
same Microsoft 365 connection your personal assistant uses. The recipient sees you, replies
come straight back to you, and the message is in your own Sent Items where you would look
for it.

If you have not connected Microsoft 365 yet, the dialog says so and offers to connect —
it will not quietly send a client-facing document from a generic portal address. Sending
from the shared mailbox stays available as a deliberate choice, and then replies are still
addressed to you.

## Embedded views now survive the trip

The important part is what stops going missing. A page can embed a live view — a grid
of link-preview cards, a catalog, a table pulled from the mesh — and until now every
export dropped it. The PDF and DOCX pipelines never understood the embed syntax, so an
embedded view was printed as its own raw source text; the print-to-browser path emitted
the empty placeholder a browser would have filled and printed a blank space.

Emailing a document resolves those views on the server and writes them into the message
as real content. A card grid arrives as cards, with their titles, descriptions and links
intact.

## Built for mail clients, not browsers

Outlook on Windows draws email through Word, which quietly ignores much of what a page
relies on. The document is rewritten for that reality:

- **Tables get real column widths**, sized to how much text each column actually carries
  and set on every cell — Word ignores the usual column definitions entirely.
- **Layout is table-based** and all styling is inline, because Word has no flexbox or
  grid and mail services strip stylesheets.
- **Links are absolute**, so they still work when clicked from an inbox.
- **Pictures travel inside the message** and appear immediately, instead of waiting
  behind "Download pictures" or being dropped as unsupported.
- Vector icons, which Word renders as a broken-image box, are left out rather than sent
  broken.
