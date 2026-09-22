---
Name: Shared Rule Blocks
Category: Architecture
Description: How the fleet's seven AGENTS.md files are held identical where they overlap — the marker syntax, the register, the two hubs, and why the drift gate sweeps every repo from one place instead of each repo checking itself.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 3h5v5"/><path d="M8 3H3v5"/><path d="M21 16v5h-5"/><path d="M3 16v5h5"/><path d="M9 9h6v6H9z"/></svg>
---

# Shared Rule Blocks

**`AGENTS.md` is not documentation. It is executable context.**

Every repo in the fleet has a `CLAUDE.md` that is a single line — `@AGENTS.md`. So `AGENTS.md` is
not reference material an author consults when they remember to; it is the instruction file loaded
into every agent's context, in that repo, on every session. Two repos carrying different text for
the same rule does not mean the docs are untidy. It means **agents behave differently per repo**,
silently, in a file that no build, no test and no compiler ever reads.

Seven repos carry overlapping copies of the same rules. Until 2026-08-30 the only thing holding
them together was prose — the satellites say *"everything there applies here unchanged"* — and the
habit of hand-carrying each edit to every repo. `MeshWeaver.Plugins#705` measured what that
produced and asked for a decision. This page is the mechanism that came out of it.

## The worked example, and why prose could never have caught it

On 2026-08-30 a maintainer directive — *always conserve work products* — was rolled into all seven
repos as seven separate pull requests: core#2732, Plugins#954, Education#236, Reinsurance#122,
SocialMedia#111, Manufacturing#34, Memex#149.

That rollout was correct. Nothing about it was checked. Had one repo been missed, or had one
drifted the week after, no signal anywhere would have said so — and every agent session in that
repo would have run under a different rule without anyone noticing.

Measuring the seven copies afterwards also showed the rollout was **less identical than it was
believed to be**. The intent was "byte-identical apart from a per-repo doc-home clause". In fact
there were three variable regions, not one, and core hard-wraps its prose at ~200 columns while the
satellites wrap at ~95. Both facts matter for the design below.

## The two hubs

`#705` asked whether `MeshWeaver.Plugins` is the hub for everything. It is not, and the files
already say so themselves:

| Kind of rule | Hub | How the files declare it |
|---|---|---|
| **Authoring** — the plugin mechanism, node-per-file mapping, Store shape, provisioning | `MeshWeaver.Plugins` | Manufacturing, SocialMedia and Reinsurance each open with *"The authoritative authoring rules live in MeshWeaver.Plugins' `AGENTS.md` … Everything there applies here unchanged"*. Education names the same file for the authoring subset it uses. |
| **Platform / process** — conserve work products, CI, deployment topology | `MeshWeaver` (core) | The conserve block says so in its own text, in all six satellites: *"the platform rule lives in MeshWeaver's AGENTS.md"*. Core also owns the shared reusable CI and the deployment topology. |

So **`MeshWeaver.Plugins` is a hub for authoring and a spoke for platform.** The hub is a property
of the *block*, not of the repo — which is why the register records a hub per block rather than one
hub per repo.

A third case exists and is deliberately not resolved: rules replicated **across satellites with no
canonical home at all** (`#705`'s "problem B"). Those need a maintainer decision about where they
should live. Until that decision is made the register can still hold them to *agreeing with each
other*, which is a claim that needs no decision — see `hub: peers` below.

## The marker syntax

A checker cannot compare regions it cannot find, so shared regions are delimited. Both markers are
HTML comments, so every rendered view is unchanged and the raw file stays readable:

```markdown
<!-- shared-rule:begin conserve-work-products -->
**🗂️ ALWAYS conserve work products — …** The durable form is <!--slot:doc-home-->a doc page under
`src/MeshWeaver.Documentation/Data/`<!--/slot--> — issue comments, PR bodies … Maintainer
directive, 2026-08-30<!-- shared-rule:end conserve-work-products -->. This rule holds in EVERY repo…
```

**Block markers** delimit the compared region. They usually take their own line; an *end* marker
placed mid-sentence — as above — is the right move when a sentence's tail is role-specific. The
conserve block deliberately stops after the date stamp, because the date is the cheapest way to
spot a stale copy and the clause after it correctly differs at each end of the same pointer (core:
*"this rule holds in EVERY repo of the fleet"*; a satellite: *"the platform rule lives in
MeshWeaver's AGENTS.md"*).

**Slot markers** are inline and mark the spans that are legitimately per-repo. The checker replaces
each with the token `<<name>>` before comparing, so a slot's **content** is free while its **name**
and **position** are asserted like every other byte. An empty slot is legal and meaningful: it
records that this repo says nothing where another says something.

Comparison is byte-for-byte after exactly **one** normalisation: markdown soft-wrap is collapsed.
That is the removal of a loophole rather than the addition of one — core wraps at ~200 columns and
the satellites at ~95, so a raw byte compare would encode each repo's wrap width as part of the
rule and be red on day one over a difference no reader can see. Everything a reader *can* see —
wording, punctuation, emphasis, links, emoji — is compared exactly.

## The register

`.github/shared-rules.json` in core lists every shared block, its hub, and every repo required to
carry it. **It lives in the hub, not in the spokes, and that is the anti-defeat property**: a
satellite cannot opt itself out by deleting its own markers, because the register — which it does
not own — still says it must carry the block, and a listed repo with no markers is red.

`hub` takes a repo, or the literal `"peers"`:

- **a repo** — that repo's copy is authoritative and every spoke must match it.
- **`"peers"`** — the listed repos must agree with each other while no canonical home has been
  decided. It freezes an existing agreement against further drift **without pre-empting where the
  rule should eventually live**. When the maintainer does decide, changing `hub` to that repo turns
  the same gate from enforcing sameness into enforcing the direction of inheritance.

## Why the sweep runs in one place

The obvious design is for each repo to check itself against the hub. **That design structurally
cannot detect the case this gate exists for.**

A per-repo self-check only runs when that repo has a pull request. If a directive is rolled to six
of seven repos, the seventh has no pull request, so its gate never runs — and *"six of the seven
were updated"* produces evidence identical to *"all seven were updated"*. Only a sweep that reads
every repo from one place can tell those apart.

So the gate is one job that reads all seven, and it runs in core — which owns the shared CI and is
the platform hub. It runs in two places:

| Where | When | Reads |
|---|---|---|
| `dotnet-test.yml` → job `shared-rules` | every core pull request and push | core from the checkout (so a PR is judged on its own diff), the other six from their default branches |
| `shared-rules.yml` → job `sweep` | daily at 05:45 UTC, on dispatch, and on a main push touching the gate | all seven from their default branches |

The scheduled half exists because drift does not need a pull request to appear. In a week when the
satellites are busy and core is quiet, the fleet could be inconsistent for days with every check
green — because no check ran. That is the gate's own defect one level up.

### The coupling this creates, chosen deliberately

The pull-request half reads the **satellites' default branches**, so a drift merged in a satellite
turns core's required check red and blocks core merges until it is fixed *there*.

That is the point, not a side effect — it is the same coupling the i18n mirror guard already
carries, where a Plugins PR stays red until the core catalogue lands. A rule that differs between
repos means agents follow different instructions in each of them, which is exactly what must not
merge. **The remedy is always a one-pull-request fix in the repo the error names.** There is no
bypass, and adding one would make the gate decorative.

## No skip-trapdoor

Six of the seven repos are private, so this gate genuinely needs a credential — precisely the
situation *"A gate NEVER tests its own inputs"* warns about, because GitHub paints a skipped job
with the same tick as a passed one. So:

- the credential is **asserted**, and the job fails RED naming what to provision. It never asks
  *"is the secret set?"* in order to decide whether to run;
- **present is not valid.** The token is proven to actually READ every repo in the register before
  any verdict is trusted. `chart-drift` learned this on 2026-08-17, when a provisioned-but-dead
  credential passed an emptiness test and every run afterwards died at checkout while the gate
  reported its inputs were fine;
- the checker's **self-test runs first** and fails the job. Eighteen cases prove it fires on each
  defect — a changed word, dropped emphasis, a missing block, a removed or unbalanced marker, a
  renamed slot, an undeclared slot invented in `AGENTS.md`, prose moved *into* a slot to dodge the
  comparison — and stays silent on each legitimate difference, including a re-wrap;
- there is no `continue-on-error:`, no input-shaped `if:`, and no path filter;
- every unreadable input is a failure. An unfetchable repo, a hub whose own copy is missing, an
  empty register and an unparseable register are each red, because a gate that reports "no drift"
  on evidence it does not have is worse than no gate at all;
- the one exemption is expressed on the **event**: a pull request from a fork cannot mint the token
  by GitHub's design. It is written once, on the event, never on the secret.

The credential is a GitHub App installation token minted per run (`FLEET_READER_APP_ID` +
`FLEET_READER_APP_PRIVATE_KEY`, the org's READ-ONLY `fleet-reader` App — see
[GitHub App Credentials](../GitHubAppCredentials)), scoped to `contents: read` on
exactly the repos in the register — never a stored PAT, which has no owner, no expiry anyone
watches, and fails indistinguishably from a scope problem.

## Adding or changing a shared block

**Adding one:** the gate is red for a repo that is listed and has no markers, so a half-landed
adoption cannot be mistaken for a pass — and it is ALSO red for a marker whose id the register does
not declare, in any of the seven repos it reads. So neither "satellites first" nor "core first"
lands green in one step. **Land it in three:** (1) a core change that wraps the hub's copy and
registers the block with `required-in` naming only the hub — green on its own, and the id becomes
known; (2) the spokes' copies, in any order; (3) a core change widening `required-in` to every repo.
`never-hand-roll-ui` (2026-09-15) was the first block landed this way; `dispose-to-serve-new-state`
and `file-it-and-move-on` followed it.

🚨 **Step 3 is the dangerous one, and the danger is silent until it is too late.** Widening
`required-in` before a spoke carries the block turns that spoke's gate red on **every** pull request
in that repository — including the one that would add the block. Run as a deliberate red control
before `file-it-and-move-on`'s step 2 merged, the widened register named **all six** spokes MISSING
while the other five blocks each read `✓ 6 spoke(s) match the hub` — six errors, one per repository,
which is exactly what a premature step 3 does to the fleet. Run again after the six merged, the same
command read `✅ All 6 shared rule block(s) are identical everywhere they are carried, across 7
repos`. **Do step 3 as its own change set, after confirming all six, and read the spokes from their
default branches rather than from a local copy** — the gate does that by default, and `--local` for
the repo the gate runs in is what makes a pull request judged on its own diff.

**The register lists seven repositories, not eight, and that is a decision rather than an omission.**
`Systemorph/MeshWeaver.Crm` carries an `AGENTS.md` and is deliberately absent from `repos`, so this
gate never reads it and no block can require it. Admitting it has a hard prerequisite: the gate fails
CLOSED on a repository whose `AGENTS.md` it cannot read, so the read-only fleet-reader App has to be
installed on Crm first — an organisation-level act no change set here can perform. It has a second:
because `required-in` is per block and a listed repository with no markers is red for **each** block
naming it, Crm has to carry every block already registered before it is listed. So adding it is its
own rollout, and a reader who "tidies up" the missing line reddens the gate on an unreadable
repository. The rationale is recorded in `.github/shared-rules.json`'s own header, once.

**Changing the text of one:** the hub's copy is authoritative, so the hub's change merges first and
every spoke's pull request stays red until it does. Never "fix" that red by reverting the hub.

**What a slot is for:** a span that is genuinely per-repo — a doc home, a module list, an instance
name. A slot is an exemption from the comparison, so it may only be created in the register, never
by editing `AGENTS.md`; the checker refuses a slot the register does not declare, which is what
stops "add a slot around the part I changed" from becoming the way around the gate.

## 🚨 The other axis: a rule and the SKILL it mandates, inside one repo

This register holds a rule's copies identical **across repos**. It says nothing about the second way
the same rule splits — **within** a repo, between `AGENTS.md` and the `.claude/skills/**` file it
mandates.

That gap was live, and it is the reason `AgentsRuleAndSkillAgreementGuard` exists (#4965). #4964
changed the What's New rule from *per user-facing change* to *per release* in `AGENTS.md` and in
`ReleaseProcess.md`, and did not change `.claude/skills/pullrequest/SKILL.md`, whose step 0.5 still
opened with *"every USER-FACING PR ships a 'What's New' entry"* and still carried the minting
apparatus — `DATE=$(date -u +%Y-%m-%d)`, the front-matter `printf`, `git add "$NOTE_FILE"`.

**`AGENTS.md` does not merely reference that skill — it requires it** (*"automatically follow the
`pullrequest` skill"*). So for as long as both stood, an agent received two contradictory rules and
the operative one was whichever it read last, with the skill holding the copy-pasteable commands. CI
was green throughout. The automatic reviewer caught it by hand.

The two guards that already read `.claude/skills/**` check something else — `GuidanceBridgeRatchetGuard`
ratchets `.ToTask(` inside ` ```csharp ` fences, and `SkillFrontMatterGuard` checks front-matter
shape — so nothing compared a skill's *content* against the rule that mandates it.

### Why it is a ratchet of literals and not a diff

*"Do two prose documents agree?"* is not mechanically decidable, so the guard does not try. It is a
table of **(rule, forbidden literal)** pairs plus one **shape**, which is the cheaper of the two forms
#4965 proposes and the one that catches what happened.

**The property that makes a table of literals safe to keep is that it cannot go stale silently.** A
forbidden-literal table is a statement about today's wording: retire the rule and the entry permits
nothing, catches nothing, and reads as coverage. So every pair also names **evidence** that its rule
is still stated in `AGENTS.md` — for `whatsnew-cadence`, the policy id itself — and a pair whose
evidence has gone is **RED, not inert**. Retiring a rule and retiring its pair are one change set.
That is the same discipline the transitional allow files use, and the same failure this guard exists
for, applied to the guard.

### The shape, and the version of it that was wrong

The literal half catches a relapse that reuses the old wording. The shape half catches one that does
not: **no skill may CONSTRUCT a path under `WhatsNew/`.** Naming the path in prose with placeholder
metavariables — `WhatsNew/<yyyy-MM-dd>-<slug>.md`, which the *release* procedure legitimately does —
is fine.

🚨 The first version of that pattern looked for `printf` / `cat >` / `git add` on the same line as
`WhatsNew`, and **it missed the real defect**: the apparatus assigned the path to a variable and every
verb afterwards named the *variable*, not the path. Its own positive control is what said so. The
discriminator that works is a `WhatsNew/` path carrying a **shell interpolation**, because only a line
building a filename at run time has one — and the trailing slash is load-bearing, since without it the
pattern matches `WhatsNewEntryIntegrityTest` on a line that also quotes an anchored regex ending in
`$`.

Both detectors carry a positive control that feeds them the defect verbatim and then the corrected
text, because both are expected to find nothing in a healthy tree and neither could otherwise tell
*clean* from *broken*.

## What this does not do

It does not decide what the canonical text of any rule *should* be. It holds copies identical; it
has no opinion on which copy is right. `#705`'s comment of 2026-08-27 is the case in point — the
older copies of a drifted section turned out to be closer to correct than the one that grew, and a
promote-and-copy would have propagated the newer, wrong text into four repos at once with more
confidence for being canonical. Enforcing sameness is safe; deciding the wording is a maintainer's
call, and a `hub: peers` entry is how a block waits for one.

## Related

- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why a skipped or absent required context counts as
  satisfied, which is the defect class this gate is built to avoid.
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — how a page like this one is written and
  how its links resolve.
