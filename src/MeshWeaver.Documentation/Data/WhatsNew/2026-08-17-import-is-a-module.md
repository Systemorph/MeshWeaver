---
Name: Excel and CSV import is a module
Category: Feature
Description: The import stack and its Excel/CSV readers moved into a MeshWeaver.Import module, so a deployment that never ingests spreadsheets no longer carries it.
Icon: DocumentTable
Order: -20260817
---

# Excel and CSV import is a module

Tabular import — the Excel and CSV readers, the mapping configuration, and the handler that turns
an uploaded file into typed entities — now ships as `MeshWeaver.Import` instead of being compiled
into every portal. Along with it goes the whole `MeshWeaver.DataSetReader` family, which nothing
else in the platform uses.

Every portal shipped today lists it, so nothing changes for anyone importing spreadsheets. What
changes is that a deployment which never ingests one can leave it out.

Content keeps working the same way. Node sources that write `using MeshWeaver.Import` — a pricing
sheet's import configuration, for instance — still compile: a listed module joins the same
reference set the platform's own assemblies are on. That is why the module is listed first, ahead
of anything that compiles against it.

One thing this is honestly *not*: a smaller image. The spreadsheet libraries themselves are used
elsewhere in the portal and stay exactly where they were.
