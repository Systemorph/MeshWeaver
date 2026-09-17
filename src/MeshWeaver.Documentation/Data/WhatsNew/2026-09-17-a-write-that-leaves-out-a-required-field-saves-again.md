---
Name: A write that leaves out a required field saves again
Category: Fix
Description: Saving a node without one of the fields its type requires failed with a raw, untranslated serializer message. Such a write was never meant to be judged at all, and it now goes through.
Icon: Bug
Order: -20260917
---

# A write that leaves out a required field saves again

Yesterday's check that [content fits its type](/Doc/Architecture/ContentSchemaOnWrite) was meant to
say nothing about a write that simply leaves a required field out — that is what a partial save looks
like, and it was never the thing the check was built to catch.

It said something anyway. A save whose content omitted a field the type marks as required came back
with the serializer's own words — *"JSON deserialization for type … was missing required properties
including: 'content'"* — in English, whatever language you read the portal in, and the save did not
happen. Assistants writing to a node through the mesh tools hit it first, because a partial update is
their ordinary shape.

Those writes land again, exactly as they did before the check existed. Content in which **nothing**
matches the type is still refused, and still says so in your language, naming the fields — that part
was and remains the point of the check.
