---
Name: A request filed by a script or an agent now runs
Category: Fix
Description: Asking for something by creating a node — from an agent, a script, the API or a webhook rather than a page — filed the request correctly and then nothing happened, with no error anywhere, until somebody happened to open that node.
Icon: PlayCircle
Order: -20260903
---

# A request filed by a script or an agent now runs

Several things in the platform are asked for by **writing the request down**: you set "activate this
subscription", "enrol this person", "cancel this job" on a node, and the part of the platform that
owns that node notices and carries it out. That is the normal way work gets requested here, and it
is deliberately the same whether the request comes from a page, an agent, a script, the API, or a
billing webhook.

It only actually ran when the request came from a **page**.

The piece that watches for these requests starts when the node it belongs to is first opened. A page
opens the node as a side effect of showing it, so the watcher was already running and the request
was picked up within seconds. A request created any other way wrote the node correctly — and then
nothing opened it, so nothing was watching, and the request sat there. Not failed, not queued:
**sat**, with no error, no failed status, and nothing in any log that would have said so. It would
then run, correctly and immediately, the next time anybody happened to look at that node — which
could be minutes or hours later, or never.

One measured case: a subscription activated by an agent sat untouched for **four and a half hours**,
then completed twenty seconds after the first person opened it. The same subscription created from
the admin page had activated in thirty-one seconds. The only difference was who asked.

The people this hit are exactly the ones least able to see it: an operator running a script, an
agent doing a task on your behalf, and an automated payment confirmation. The visible symptom was
somebody who had been granted a plan and still saw the paywall.

**A node created with a pending request now wakes its owner**, so the watcher sees the request and
acts on it straight away regardless of who filed it. Two details worth knowing:

- **It happens after the create is confirmed**, so this cannot slow down, fail, or block creating
  anything. If the owner cannot be woken, the request is left exactly as filed and the reason is
  recorded rather than swallowed.
- **Only nodes that actually carry a pending request wake anything.** Ordinary content — a page, an
  image, a document — is not a request and starts nothing, so bulk imports are unaffected.
