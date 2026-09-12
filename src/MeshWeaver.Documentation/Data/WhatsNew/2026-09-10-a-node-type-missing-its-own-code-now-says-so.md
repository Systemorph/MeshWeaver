---
Name: A node type missing its own code now says so
Category: Fix
Description: A node type whose own code files are gone used to report itself as broken C#, naming types and methods that do not exist anywhere — and tried the same impossible compile again on every restart. It now names the missing code directly, and stops repeating an attempt that cannot succeed.
Icon: Checkmark
Order: -20260910
---

# A node type missing its own code now says so

A node type points at the code files it is built from — usually its own `Source` folder, often a
shared library as well. When **its own** files are missing but the shared ones are still there, the
compiler was handed part of the picture and reported what it saw: unknown types and unknown methods,
by name.

Those names looked like a real programming error. They were not — every one of them was defined in a
file that was simply not there. On a live portal one node type sat like that for four days while the
names it reported were searched for in modules that never contained them, and every restart ran the
same compile again and produced the same three messages.

## What changes

**The failure now leads with the actual cause.** The recorded error starts by naming which of the
type's declared code locations found nothing, so the first line you read is *"this location is
empty"* rather than *"this type does not exist"*. The compiler's own messages are still underneath —
they are the evidence, they were just never the explanation.

**The compile activity says the same thing while it happens**, in your language, above the compiler
output — so you can see it on the run itself, not only afterwards on the record.

**An attempt that cannot succeed is not repeated.** Missing code files are a fact about your content,
not about the platform version, so no update and no restart can supply them. The first attempt still
runs and still reports honestly; after that the type keeps serving its recorded error instead of
spending a compile on it at every restart. The log says so plainly, and says what will actually help.

**It fixes itself the moment the files come back.** The check is re-asked against what is really
there each time, so restoring the code — by re-installing, re-copying, or creating it — starts a
fresh compile on its own. Nothing has to be reset, cleared or pressed.

**A missing file no longer holds up an update.** A type broken this way is now reported as a content
problem rather than as a fault in the new platform version, so an installation is no longer kept out
of service over code that is missing regardless of which version it runs.

## What this does not change

**Real compile errors still behave exactly as before.** A type whose code is all present and does not
compile is still reported as broken, still retried when the platform or its modules change, and still
holds back an update. Only the "the code is not here" case is separated out.

**A type that has never compiled successfully is untouched.** If it never worked, nothing was lost —
its failure may genuinely be its own configuration, so it keeps being retried and keeps being
treated as a fault.

**Empty test folders are still normal.** A node type with no tests is not reported as missing
anything.

**Copying a node type can still leave its code behind.** This change makes that situation obvious and
cheap instead of silent and repeated; it does not yet prevent a partial copy. That is being fixed
separately.
