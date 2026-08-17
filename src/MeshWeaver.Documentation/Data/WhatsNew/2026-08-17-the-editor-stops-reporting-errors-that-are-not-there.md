---
Name: The editor stops reporting errors that are not there
Category: Fix
Description: Code diagnostics now use the same import scope the real compiler does, so a NodeType source that builds and ships is no longer flagged with errors it does not have.
Icon: CheckmarkCircle
Order: -20260817
---

# The editor stops reporting errors that are not there

Opening a NodeType source could show a row of red errors — `The type or namespace name
'IMeshService' could not be found`, `The non-generic method 'IServiceProvider.GetService(Type)'
cannot be used with type arguments` — on code that compiles perfectly and is already running in
production. The compile status for the very same NodeType said *"the assembly was built without
errors and is loaded"*. Both came from the platform, in the same minute, and they disagreed.

The editor was wrong. When a NodeType is built for real, all of its source files are combined into
one file that begins with the framework's standard imports, so every file can use `IMeshService`,
`Controls`, `GetService<T>` and the rest without importing them itself. The diagnostics service
instead looked at each file on its own — which is what lets an error point at the exact file and
line you are editing — and in C# an import only covers the file it is written in. So the standard
imports covered nothing, and every source that relied on them was reported broken.

Diagnostics now build that same import scope explicitly, including the imports declared in
neighbouring files, which the real compiler also shares out. Errors keep pointing at the precise
file and line as before; there are simply no longer any phantom ones. If you have been adding
`using` lines to silence errors the build never had, they were never needed.

The lesson we took from it: a compile verdict is only trustworthy when the thing reporting it is
the thing that compiles. The diagnostics path now derives its imports from the same declaration the
build uses, so the two cannot drift apart again.
