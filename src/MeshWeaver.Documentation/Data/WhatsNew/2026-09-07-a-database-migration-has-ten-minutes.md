---
Name: A database migration has ten minutes; more is a decision written into the deployment
Category: Fix
Description: The migration Job carries a ten-minute budget enforced inside the process and by Kubernetes; a deployment that needs more sets migration.budgetMinutes explicitly.
Icon: Timer
Order: -20260907
---

# A database migration has ten minutes; more is a decision written into the deployment

The migration Job that runs on every deploy now carries a **budget**: ten minutes for every schema
step, versioned repair and maintenance phase together, unless the deployment's values say otherwise
(`migration.budgetMinutes`). The number is enforced twice from that one setting — inside the process,
where the runner stops and names the step that outlived it, and outside, where Kubernetes ends the Job
at the same deadline. A deployment that needs more writes the number into its overlay; nobody gets more
by default.

The rule comes from a measurement. On 2026-09-07 the public instance's migration Job had been running
for four and a half hours: the schema work had finished in seventy seconds, and the rest was an
embedding backfill making one request and one update per row across 220 partition schemas, with the
deploy that started it long since reported as failed and nothing anywhere naming the cause. A
migration that needs longer than the budget is not one to make room for; it is one to rewrite as bulk
work — one set-based statement per partition, never a request per row. The backfill itself now runs in
batches and finishes in minutes.
