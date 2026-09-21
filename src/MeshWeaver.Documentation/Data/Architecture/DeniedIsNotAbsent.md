---
Name: Denied Is Not Absent
Category: Architecture
Description: A Forbidden that a read discards comes back as the thing being missing, so the operator announces that a node pool, an Ingress or a ConfigMap does not exist when it was merely not permitted to look. The three answers a read owes its reader, why fixing the sites a bug report names does not sweep the defect, and the gate that makes the next one red.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M5.6 5.6l12.8 12.8"/><path d="M12 8v1"/><path d="M12 15v1"/></svg>
---

# Denied Is Not Absent

A read has **three** answers, not two:

| | what is true | what the reader should do |
|---|---|---|
| **PRESENT** | it is there | carry on |
| **ABSENT** | it is not there | create it, or refuse naming it |
| **REFUSED** | *nothing is known either way* | fix the grant; **rule nothing out** |

`2>/dev/null` on a read throws away the one fact that decides which of the last two is true. The
command substitution yields empty, the `||` branch fires, and a **permission error comes back as a
statement about the world**:

```bash
# the defect, in the shape it keeps appearing in
value="$(kubectl get thing 2>/dev/null)"
[ -n "$value" ] || hosting::die "there is no thing"
```

The operator then tells its reader that a platform layer, an Ingress or a ConfigMap does not exist,
and sends them off to re-create something that was there the whole time. It is the same confusion
the data plane was bitten by when a `Not found` that meant *you may not read this* closed an issue
that was re-filed unchanged four weeks later — here it is in the control plane, in the one output an
operator is supposed to trust.

## The failure mode is that the fix does not sweep

The original report measured three probes in `hosting-db-release`'s platform-layer preflight. They
were fixed, with behaviour tests, by a local `probe` helper **inside that one script**. The
discrimination was then reachable from exactly one file — and so:

- the **credentials-Secret read forty lines below it, in the same file**, went on collapsing the two
  answers. It sat inside a pipe (`… 2>/dev/null | wc -c`), where no `||` could have caught it
  either. Measured before the sweep, all three of *Forbidden*, *Secret absent* and *Secret present
  with no password key* produced one sentence, byte for byte:
  `its credentials Secret <release>-app carries no password … Read the operator's log in
  cnpg-system.` A missing ClusterRole grant therefore sent the reader to CloudNativePG's log, and
  the operator stated the contents of a Secret it had never read.
- `hosting-deploy`'s adoption loop counted a Forbidden as **ABSENT — "to be created"** — in a number
  it reports. Its `auth can-i` preflight asks about `create`/`patch`/`delete` and never `get`, so
  nothing else would have caught it.
- `hosting-redirect` answered `no Ingress in <ns> — nothing to redirect` for a refusal, about an
  instance whose Ingress was serving traffic.
- `hosting-verify-catalog` answered `the release did not render one` — a **verdict about the chart**
  for a permission the Job did not hold.
- `hosting-registry-key` answered `<secret> carries no <key> — there is no key there to revoke`.

**A pattern fix must sweep every call site**, and the way to make that true later as well as now is
to put the discrimination where every script reaches it and then compare the tree against it.

## The primitive

`hosting::probe` in `deploy/aks/operator/bin/_common.sh`. It returns `0` present, `1` absent, `2`
refused, and hands the caller `HOSTING_PROBE_OUT` and `HOSTING_PROBE_ERR`.

```bash
hosting::probe any kubectl -n "$ns" get secret "$name" -o json
case $? in
  2) hosting::die_refused "Secret ${name} in ${ns}" ;;   # says what is NOT known
  1) hosting::die "no Secret ${name} in ${ns} — …" ;;
esac
value="$HOSTING_PROBE_OUT"
```

Two details that are load-bearing:

- **`any` vs `output`.** `kubectl get nodes -l workload=db` exits 0 and prints nothing when the
  selector matches no node. That is an ABSENCE, not a failure, and only the caller knows whether
  empty output means absent.
- 🚨 **`hosting::probe … || hosting::die "…absent…"` is the defect wearing the fix's clothes.** A
  single `||` merges 1 and 2 straight back together. Branch on all three, and let the refusal say
  that nothing was ruled out — which is what `hosting::die_refused` exists to keep uniform.

`hosting::secret_value` and `hosting::inline_setters` return `2` on a refusal for the same reason. A
caller that only tests for success is unaffected: both are non-zero.

## The gate

`deploy/aks/operator/test/check-stderr-discarded.sh` enumerates every line in `bin/` that names
`kubectl` **and** discards its stderr, and requires each to be declared in
`test/stderr-discarded.allow` with the reason collapsing the two answers is safe *there*.

- an **undeclared** call is RED, naming the script, the verb and the resource, with `hosting::probe`
  as the remedy;
- a **declaration whose call is gone** is STALE and RED, so the list can only shrink deliberately.

A line belongs in the allow file only when a refusal **cannot reach the reader as a statement about
the world** — because the failing branch does more work and surfaces the refusal itself (an
ensure-if-absent whose `create` carries its own loud Forbidden), because the message names the READ
rather than the thing (*"could not read deployment/X"*), or because a preceding read under the same
grant already proved the permission. Eight calls qualify today. **If a refusal could come out as
"there is no X", the fix is the primitive, not a line in the file.**

This is the same argument `check-rbac-coverage.sh` makes one directory over, and it has the same
history behind it: twice a script reached main without its grant and care did not catch either.

## What a gate cannot see, stated so nobody takes green for more

A statement whose `kubectl` and whose redirect sit on **different lines**; a redirect built from a
variable; and everything `helm` and `az` do with their own stderr. Those stay covered by review.

## The other half: a grant is not a grant until it reaches the cluster

The ClusterRole is `deploy/aks/manifests/hosting-operator/operator-rbac.yaml` **in this repository**,
and **a Job never widens its own RBAC**. It reaches the cluster only when the config repo's
`helm-release` lane applies it, at the pin that lane renders the chart from. So a grant can be on
`main`, reviewed and gated, while every Job in the cluster still runs under the older role — which
is exactly the window in which these messages matter most, and exactly when the wrong sentence sends
the reader somewhere there is nothing to find.

Read the running state, never the merge: a grant's presence in this file says what the operator
*will* be allowed to do after the next lane run, not what it may do now.

## See also

- [Deployment (AKS)](/Doc/Architecture/DeploymentAKS) — how the operator, its image and its manifests reach a cluster
- [Operating From The Portal](/Doc/Architecture/OperatingFromThePortal) — why an operation is an `InstanceAction`, and what `kubectl` is reserved for
- [A Census That Counts Must Name](/Doc/Architecture/ACensusThatCountsMustName) — the sibling defect in the data plane: an answer that reads as clean because the identity was dropped
