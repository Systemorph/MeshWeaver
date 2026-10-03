---
Name: End Vision — Jobs, Durable Streams and Document Ingestion
Category: Architecture
Description: "The authoritative design decided on 2026-10-03: every activity is a job in a queue on control, queues and jobs are nodes, durable streams are saved nodes with checkpoints, logs and documents are chunked nodes, triage acts, and — the full monty — a global admin attaches a blob container to a partition and the mesh ingests it exactly once into labelled, indexed chunks. With the complex job flows, the two executable e2e specs, and THE PLAN: every gap ranked, owned and tied to the assertion that proves it."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h10l6 6v10H4z"/><path d="M14 4v6h6"/><path d="M8 14h8M8 17h5"/><circle cx="18" cy="18" r="3"/></svg>
---

# End Vision — Jobs, Durable Streams and Document Ingestion

**This page is the one authoritative place for the architecture decided on 2026-10-03, and its
[plan](#the-plan) is THE plan.** The per-module pages are the detail and link back here; when a
pull request lands one of the pieces below, it updates its row in the plan in the same change.

The maintainer's end vision, verbatim:

> 1) in the gui, being a global admin, I can attach a new blob container and give it a string name
> and choose to which partition to assign
> 2) We create the table on this partition (needs rights). if we fail ==> park the process until
> unblock. set event subscription to durable stream to get notified when unblocked. example: create
> governed activity and try to assign to anyone with rights to create. let user tell you whom he
> wants to pick. when approved, partition is created and we continue to 3)
> 3) we start ingesting the storage. this can be started at any time by sending some
> InitializeRequest. Make robust => if table not created, go to 2) etc. once pre-condition met,
> start activity (which should be started on control instance in build queue) to loop through all
> docs, extract via pdf pig and what have you and then label the data re persons, especially which
> function, e.g. patient vs doctor vs relative..., addresses, types of message (e.g. request for
> policy, claims report, ...), for picture, it creates some extractor to do image recognition etc.
> ==> for these tasks, try to set up *local* models. we will then co-ordinate all inside the build
> queues which can spawn more build queues, e.g. to find other docs. ==> the mesh will guarantee
> thread safety and that each doc is only parsed once. ==> write dedicated tests for this.

And: *"create dedicated e2e suite for complex flow"* — ask-and-await, retry-then-flag,
triage-unblock and correlation, [below](#complex-flows).

## Decisions

Each decision names where its detail lives. Nothing here is optional per tenant: every default is
in code, so a new tenant needs zero configuration.

### 1 · Every activity is a job in a queue on control

- **Nothing executes outside a queue.** Every activity launch — an agent round, a build, a
  babysitter pass, an ingestion, a fix — enqueues a **job** on the control instance, triggered by an
  event, never by a loop.
- **Queues and jobs are ordinary NodeTypes** (`Hosting/Queue`, `Hosting/Job`), mesh members with
  mesh addresses. **A queue's own message hub IS the queue**: on Orleans it is one grain, so its
  passes are ordered and thread-safe without a lock. Everything durable is a node instance; a roll,
  recycle or dispose loses nothing.
- **Tiers and priorities.** Every tenant gets a `default` queue on first use; the operator estate
  has several. Order: **express** (anything blocking — a red main, a red required gate, an outage,
  a stuck CD; highest priority, reserved capacity, pre-empts admission) > **babysitter** (red PRs,
  stale gates, drafts — interrupt, heal, fix) > everything else.
- **Control is the MAIN queue and ROUTES.** Each job goes to an **execution queue** on a
  pre-installed instance whose architecture, platform and module versions are **>=** the job's
  requirements. Nothing is ever built for a job; the target is configurable, a disposable mesh is
  an opt-in target.
- **One fluent job declaration, complete defaults**: trigger events, queue, priority,
  requirements, agents + `installPaths` (installed in the execution context), the model ladder
  (Opus manages and triages; in debug/fix Opus is only the LAST rung), budgets, **retries (default
  5, with backoff)** and timeouts.

Detail: Plugins `Hosting/ActivityExecutionModel` and `Hosting/Queues` (MeshWeaver.Plugins #2730).

### 2 · Durable streams are saved mesh nodes

- A stream is **sequenced item nodes** in a satellite table of the owning partition; the
  **checkpoint is a node field**; the stream node's hub delivers **in order, exactly once**;
  consumers read it inside hubs as `IObservable`.
- **Lease = a live subscription** to the job's stream. **Orphan** = no live subscriber or a stalled
  checkpoint. **Adoption** = a new subscriber resumes from the checkpoint, exactly once — the
  generalisation of the instance-action watcher heartbeat and the orphaned-rollout sweep.
- **Park = the subscription waiting for the unblocking event.** A parked job holds no slot.

Detail: [Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes).

### 3 · Logs first: one logical node, many indexed chunks

- Every log and every document is **one logical node with many doc-part chunk nodes**, written as
  each chunk completes (a download, a token stream), vector-indexed on completion. The **original
  stays in storage**; annotations attach to chunks.
- The tables live in the partition that **owns** the storage (`Admin` for global logs).
- **Instance settings get a STORAGE section**: pick an existing container or schema in the
  pre-configured storage, or create one; bindings are typed nodes; secrets only as vault
  references.

Detail: Document Parts (`Doc/Architecture/DocumentParts`, MeshWeaver #6013).

### 4 · Correlation: every event reaches its owner

A **correlation map kept as nodes** links every external artifact — issue, pull request, review
finding, check run, feedback, user question — to its owning thread, job, queue and party. Every event
on an artifact is delivered over **synced streams** (synced queries) to the OWNING party's thread or
job, which continues the work. An owner without a live subscription is **adopted** (the lease rule).
Triage notices events such as an unanswered review finding and writes them onto the artifact; the
owner gets them — never a broadcast, never "remains for a human".

### 5 · Triage acts, never reports

Every verdict becomes an action: re-run infrastructure once, enqueue a fix job up the ladder, route
a platform break, close as superseded. A red main is the express lane. A lost runner (no steps, log
404) is transient. The feature agent undrafts its own pull request; an orphaned draft (a crashed
implementer) is adopted and resumed exactly once. Stale answered or review gates are re-run first.
A draft waiting on a decision reaches the maintainer as an approval request.

### 6 · The bug-fix process

Triage and model assignment → implement → review by a **stronger** model → redo one rung up until
the top rung, then a person. A learning loop is fed by outcomes and by humans; the cost of each
process is recorded. Detail: MeshWeaver.Plugins #2694 (merged) and #2698.

### 7 · Self-diagnosis

Live instances are read — pods, `/health`, the ingress, Loki grouped by signature — without being
asked; findings are fixed or filed into triage. Detail: Plugins `/live-diagnosis` (#2726).

### 8 · Repo split

Generic mechanisms live in core and MeshWeaver.Plugins. Everything insurance-specific — the default
insurance taxonomy, insurance labelling prompts, the synthetic insurance corpus and its scenario —
lives in **MeshWeaver.Reinsurance** (`Claims/IngestionScenario`, #245), plugged into the generic harness.

## The ingestion, step by step

### Step 1 — attach

A **global admin** opens the instance settings' **Storage** section, attaches a container (an
existing one in the pre-configured storage, or a new one), names it and picks the partition it
belongs to. The button posts an `AttachStorageRequest`; the result is a typed **`StorageBinding`**
node (`Admin/Storage/{name}`) carrying the container, the partition, a vault *reference* for the
credential, and `readOnly: true` — ingestion never writes the original.

Being a global admin grants **no data access** ([Access Control](/Doc/Architecture/AccessControl)):
attaching is a platform action; what happens inside the partition needs that partition's grants.

### Step 2 — the content table, or park

The binding's content table (`document_parts` and its annotations, per content repo, in the
**owning partition's** schema) is created by the ingest's **service identity**, acting for the
attaching admin. Two outcomes:

1. **It holds the right** → the table is created, go to step 3.
2. **It does not** → the create is refused with a distinct **AccessDenied** reason (not a generic
   validation failure), and the ingest **PARKS**:
   - it opens a **governed activity** (`storage.provision-content-table`) — the candidate approvers
     are the identities holding Create on the partition, and **the user names the one they pick**;
   - its job parks on the activity's completion (`unblockOn = governed-activity:{path}`) through a
     **durable-stream subscription** — no polling, no held slot;
   - the approver signs (Governance: own write, bound to the content hash, single use); the
     activity's executor creates the table and its completion **signals** the parked job, which
     resumes from its checkpoint into step 3.

### Step 3 — ingest

`InitializeRequest` starts **or resumes** an ingestion at any time, and is **idempotent**: sent
concurrently or repeatedly it never starts a second run. It re-checks every precondition; a missing
table sends it back to step 2 (it parks — it never fails). Then:

1. An **ingest job** on control's `builds` queue pages through the container under the binding's
   ingest root and enqueues **one job per distinct content**.
2. Each document job **claims** its document — the node `{partition}/_Ingest/{binding}/{sha256}`,
   created by an atomic insert-if-absent — extracts it (PDF via PdfPig, DOCX, e-mail with its
   attachments as child documents, images through a **local** OCR/vision model), labels it and
   writes its chunks.
3. **Jobs spawn jobs**: a reference found in the text (an id resolving to a document anywhere in the
   container, including outside the ingest root) enqueues a discovery job. Cycles end because a
   claimed document is never claimed twice; a dangling reference is recorded, never a failure.
4. Labels come from the partition's **taxonomy**: persons with their **roles** (in the insurance
   default: patient, doctor, relative, policyholder, applicant, claimant, broker, insurer staff,
   loss adjuster), **addresses**, organisations, and each document's **message type** (request for
   policy, claims report, medical report, …).
5. Chunks are written as `_DocumentPart` nodes in the owning partition's table and vector-indexed on
   completion; human corrections become annotations on chunks and feed the learning loop.

### The state machine

```
Attached ──InitializeRequest──▶ CheckTable ─ right ─▶ TableReady ─▶ Ingesting ─▶ Done
                                    │                                  ▲
                                 no right                              │
                                    ▼                                  │
                         Parked(governed-activity:{path}) ──approved──┘
                                    ▲
         InitializeRequest without a table ─┘   (re-enters step 2; never Failed)

per document:  Discovered ─claim(insert-if-absent)─▶ Claimed ─▶ Extracted ─▶ Labelled ─▶ Chunked ─▶ Indexed
               (a second claim of the same content finds the node: it links the new path, parses nothing)
```

### Exactly once — how the mesh guarantees it

- **Identity is the content.** A document's node is keyed by its SHA-256, so two paths with the same
  bytes (a duplicate file, an e-mail attachment repeating an inbox PDF) are one document; the second
  path is linked onto it, nothing is parsed again.
- **The claim is the node's creation**, through `IStorageAdapter.WriteIfVersion(node, 0)` — atomic
  insert-if-absent. Concurrent jobs race on one row and exactly one wins; the losers read the
  winner's node.
- **The document's own hub owns "parsed".** Its state transitions run one at a time on that hub
  (one grain); a replayed event, a duplicate `InitializeRequest` or a recycled hub re-reads the node
  and finds the work done.
- **Durable checkpoints, not memory.** A job resumes from its checkpoint after a recycle or a roll;
  an orphaned job is adopted exactly once.

### Local models

Extraction and labelling run on **local models** as in-cluster **CPU pods**, reachable as mesh
services behind one interface (GPU later, behind the same interface): NER (GLiNER, the `s1-extract`
service), OCR/vision for images, embeddings (an OpenAI-compatible endpoint such as Ollama). Role
assignment that needs reasoning goes to a model satisfying the partition's data residency (EU by
default), and every name it returns is **grounded** in the text — what the document does not
contain is dropped.

### The default taxonomy

Roles, message types and address kinds are **typed nodes**: a default ships per package and every
partition may edit or extend its own. The generic sample uses `sender` / `recipient` / `mentioned`;
the insurance default ships with MeshWeaver.Reinsurance (`Claims/content/ingestion/taxonomy.jsonc`
today, typed nodes once the taxonomy NodeType exists).

## Complex flows

The flows every job relies on. Each is a section of the complex-flows spec.

1. **Ask-and-await.** A job parsing information finds a crucial fact missing and asks the USER: a
   durable **question node** routed to the right person, visible in their inbox. The job **parks**
   (no polling, no held slot) subscribed to its stream for the answer. The answer — a node, a stream
   item — resumes it **exactly where it stopped**, in the same attempt, with the answer. A roll or a
   recycle while parked loses nothing; an unanswered question stays parked (no timeout into failure
   unless the declaration sets a deadline).
2. **Retry-then-flag.** A failing job is retried automatically, **5 attempts by default** with a
   growing backoff (both on the fluent declaration). After the last attempt it is **FAILED**, the
   launcher is notified, and the job page offers **RETRY**. Retry is authorised (Update on the job),
   re-enqueues the **same** job id, **resets the attempt counter and increments the retry count**,
   and once the cause is fixed the job succeeds — exactly once.
3. **Triage-unblock.** A failed or blocked job goes to **triage**, which classifies **what will
   unblock it** — a permission, a content table, a platform version not yet available, a red
   dependency, a pending approval — and parks the job on **exactly that event**. An unrelated event
   does not wake it; the right one resumes it from its checkpoint, and it completes exactly once.
4. **Correlation.** An unanswered review finding on a pull request reaches the pull request's
   **owning job** through the correlation map, which answers or fixes it; with the owner dead, the
   finding is **adopted exactly once**.

## The executable specs

Both specs live in the MeshWeaver.Plugins **Testing** package (MeshWeaver.Plugins #2736) and share one kit
(`Testing/SpecKit`); the insurance scenario is MeshWeaver.Reinsurance #245. Each NodeType's nodes are **run configurations**; a run's `Spec` area executes
it and renders one row per step. **Every red row names its gap** from the plan below, and a step
whose prerequisite failed says *BLOCKED by* it instead of timing out. Requests are reached by
**type name**, so the specs compile against today's platform and are red on exactly what is missing.

**How they run — business as usual, no builds:** a scheduled job on control's queue, routed to a
**pre-installed test instance** whose platform and modules are >= the run's requirements, renders
`{run}/area/Spec` there and stamps the run's `Routing` record (which step S0/F0 asserts). A
disposable mesh is an opt-in `Target`. In the CI Tests-area gate both specs render too: their pure
cases are green, their live rows red (tolerated as known debt in `plugin-gate.allow` — the line goes
stale, and must be deleted, the day a spec turns green).

### Document ingestion — `Testing/EndVision` (run: `Testing/IngestionRun`)

| Step | Asserts | Gaps |
|---|---|---|
| S0 | the run is a job control routed to an instance offering >= its requirements | G2, G19 |
| S1 | a global admin attaches three containers under names, bound to the chosen partitions (read-only); the corpus lands in them | G4 |
| S2a | on a partition where the ingest holds the right, its content table is created | G5, G7, G20 |
| S2b | without the right, the ingest PARKS on a governed activity awaiting the approver the user named, with an unblocking event | G6, G20, G1 |
| S2c | the approver signs; the table is created; the ingest resumes on a durable-stream event (with its checkpoint) | G6, G3 |
| S3a | concurrent `InitializeRequest`s start exactly one run | G7 |
| S3b | `InitializeRequest` without a table re-enters step 2 and parks, never fails | G7, G6 |
| S3c | one document node per distinct content, each processed by its own queue job; jobs spawn jobs | G8, G1, G2 |
| S3d | every distinct content parsed exactly once; duplicates linked | G9 |
| S3e | replays — two more `InitializeRequest`s and every document hub recycled — parse nothing again | G9, G3 |
| S3f | extracted text contains each document's key phrases | G10 |
| S3g | scans are read by a `local:` image extractor | G10, G11 |
| S3h | e-mail attachments are linked to their content's document | G10, G9 |
| S3i | persons carry their expected roles (extra labels never fail) | G12, G13 (+R2) |
| S3j | addresses are labelled | G12 |
| S3k | each document carries its message type | G14, G13 (+R2) |
| S3l | references resolve to the right documents, outside-the-root ones are found by spawned reference jobs, the dangling one is recorded | G17 |
| S3m | chunk nodes exist per document and a vector search finds them in the owning partition | G15, G5 |
| S3n | the originals in storage are byte-identical | G18, G4 |

The **scenario** is data — a content folder with `expected.jsonc`, `taxonomy.jsonc` and `corpus/` —
so a domain plugs in without code: the generic sample (`Testing/content/ingestion`, generated by
Plugins `scripts/gen-ingestion-sample.py`) and the insurance one (`Claims/content/ingestion` in
MeshWeaver.Reinsurance, generated by `scripts/gen-insurance-corpus.py`: claims reports, a policy
request, a medical report naming patient, doctor and relative, a byte-identical duplicate, scanned
claim forms, e-mails with attachments, and documents reachable only by reference). All data is
invented and deterministic.

### Complex flows — `Testing/ComplexFlows` (run: `Testing/FlowRun`)

| Step | Asserts | Gaps |
|---|---|---|
| F0 | routed job | G2, G19 |
| A1 | a job is enqueued, admitted, started, checkpointed | G1, G24 |
| A2 | it asks its person: a question node routed to them; the job parks on the answer, holding no slot | F2 |
| A3 | a recycle of the job's and the queue's hubs while parked loses nothing (checkpoint, unblock, subscription) | G24, G3 |
| A4 | an unanswered question stays parked | F2 |
| A5 | the answer resumes it from its checkpoint, with the answer, in the same attempt | F2, G24 |
| A6 | it completes exactly once; a repeated success report changes nothing | G24 |
| B1 | a declaration that says nothing grants 5 attempts and a backoff | F1 |
| B2 | five failures with growing intervals end in FAILED | F1 |
| B3 | the launcher is notified; the job offers RETRY | F1 |
| B4 | RETRY without Update on the job is refused | F1 |
| B5 | an authorised RETRY re-enqueues the same job (attempts reset, retry count 1); it succeeds exactly once | F1, G24 |
| C1 | triage names the event that unblocks a blocked job | F3 |
| C2 | the job parks on exactly that event, with a live subscription | F3, G24, G3 |
| C3 | an unrelated event does not wake it | F3, G1 |
| C4 | the right event resumes it from its checkpoint; it completes exactly once | F3, G24 |
| D1 | a review finding on an artifact reaches its owning job through the correlation map | F4 |
| D2 | with the owner dead, the finding is adopted exactly once | F4, G24 |

## The plan

Ranked in **dispatch order** — a dependency order: no item starts before the items it depends on.
The same table is the code (`SpecGaps.All` in Plugins `Testing/SpecKit`, whose test pins the
order) and renders on every run node's `Plan` area. Status as of 2026-10-03.

| # | Gap | Repo · module | Size | After | Status · owner | Proven by |
|---|---|---|---|---|---|---|
| G1 | Queue + Job NodeTypes, park/Signal, tiers, express + babysitter, default queue per tenant | Plugins · Hosting/Queue, Hosting/Job | L | — | in PR · Plugins #2730 (queue agent) | S2b, S3c, A1, C3 |
| G2 | Capability routing: main queue → execution queue offering >= requirements; disposable opt-in | Plugins · Hosting/Queue (QueueRouting) | L | G1 | in PR · Plugins #2730 | S0, F0, S3c |
| G4 | Storage settings section; typed StorageBinding; vault references; AttachStorageRequest | core (contract, tab) + Plugins (AzureBlob) | L | — | not started · durable-logs/storage agent | S1, S3n |
| G18 | Read-only binding: originals untouched | core + Plugins | S | G4 | not started · storage agent | S1, S3n |
| G5 | Per-content-repo table in the owning partition (`document_parts`) + a distinct AccessDenied rejection | core | M | — | tables in PR · core #6013; AccessDenied: nobody | S2a, S3m |
| G3 | Durable streams as saved nodes with checkpoints; in-order exactly-once delivery; replay after recycle | core | L | — | not started · durable-logs/streams agent | S2c, S3e, A3, C2 |
| G24 | Lease = live subscription; orphan adoption exactly once; job checkpoint + resume | core + Plugins | M | G1, G3 | not started · nobody (#2730 leases are timestamps) | A1, A3, A5, A6, B5, C2, C4, D2 |
| G20 | Ingest under a non-system service identity acting for the admin | core + Plugins | S | — | not started · nobody | S2a, S2b |
| F1 | Retries: default 5 + backoff on the declaration; FAILED → notify + authorised RETRY (same id, attempts reset, retry count +1) | Plugins · Hosting/Queue, Hosting/Job | M | G1 | not started · nobody (#2730: default 2, no backoff, no Retry) | B1–B5 |
| F2 | Ask-and-await: question node routed to a person's inbox; park on the answer; resume from checkpoint; no timeout unless declared | Plugins · Hosting/Job + inbox | M | G1, G24 | not started · nobody | A2, A4, A5 |
| F3 | Triage classifies the unblocking event, parks the job on it, resumes it | Plugins · Hosting triage | M | G1, G24 | bug-fix triage in PR · Plugins #2698; unblock classification: nobody | C1–C4 |
| F4 | Correlation map as nodes; events over synced streams to the owner; dead owner adopted once | Plugins · Hosting/Queue + Job | L | G1, G3, G24 | not started · queue agent | D1, D2 |
| G11 | Local model services (CPU pods, one mesh-reachable interface; NER, OCR/vision, embeddings) | core (deploy via CD) + Plugins (provider) | L | — | not started · nobody (`s1-extract` exists, unwired) | S3g |
| G13 | Taxonomy as typed nodes, default per package, editable per partition | Plugins · Indexing | M | — | not started · nobody | S3i, S3k |
| G6 | Governed provision-content-table activity; candidate approvers; user-named approver; signal the parked job | Plugins · Governance + Indexing | M | G1, G3, G5 | not started · nobody | S2b, S2c, S3b |
| G9 | Exactly once per content: node per SHA-256, atomic insert-if-absent claim on PostgreSQL | core + Plugins | M | G1 | not started · nobody | S3d, S3e, S3h |
| G7 | `InitializeRequest`: idempotent start-or-resume, re-enters step 2 | core (contract) + Plugins | M | G4, G5, G6 | not started · nobody | S2a, S3a, S3b |
| G8 | Ingest activity on the builds queue: page, one job per content, jobs spawn jobs | Plugins · Indexing | L | G1, G2, G7 | not started · nobody | S3c |
| G10 | E-mail extraction with attachments; images to a local OCR/vision extractor | Plugins · ContentCollections.Indexing | M | G11 | not started · nobody | S3f, S3g, S3h |
| G15 | Doc-part chunk writer + vector index in the owning partition; annotations on chunks | core (#6013 contract) + Plugins (writer) | M | G5 | contract in PR · core #6013; writer: durable-logs agent | S3m |
| G12 | Generic person/organisation/address labelling with grounding (re-home the Parties logic) | Plugins · Indexing | M | G11, G13 | not started · nobody | S3i, S3j |
| G14 | Message-type classification | Plugins · Indexing | S | G12, G13 | not started · nobody | S3k |
| G17 | Reference discovery by spawned jobs; cycle-safe; dangling recorded | Plugins · Indexing | M | G8, G9 | not started · nobody | S3l |
| G19 | Specs as routed jobs on pre-installed test instances; test-instance provisioning (Testing installed, the two partitions, the approver fixture) | Plugins · Hosting + Testing | M | G1, G2 | specs in PR · MeshWeaver.Plugins #2736; scheduling job + test-instance provisioning: nobody | S0, F0, B4 |
| G16 | Human label corrections feed the learning loop; escalation to a stronger model | Plugins · Indexing + bug-fix process | M | G13, G15 | escalation in PR · Plugins #2698; label loop: nobody | (not yet asserted) |
| R1 | The default **insurance** taxonomy as typed nodes | Reinsurance · Claims | S | G13 | data shipped (`taxonomy.jsonc`); typed nodes: nobody | S3i, S3k (insurance run) |
| R2 | Insurance labelling prompts/extractors (roles, message types) | Reinsurance · Claims | M | G12, G14 | not started · nobody | S3i, S3k (insurance run) |

**Already landed and reused:** PDF and DOCX text extraction (`TextExtractor`, PdfPig, in
MeshWeaver.Plugins `src/MeshWeaver.ContentCollections.Indexing`); per-file SHA-256 skip and the
pgvector chunk store (`content_chunks`); embeddings via an OpenAI-compatible endpoint; governed
activities with signatures, gates and executors (Plugins `Governance`); the content-addressed build
queue for CI commits (`Hosting/Build`); the `_Inbox` at-least-once pattern; the bug-fix worker per
item (#2694) and self-diagnosis (#2726).

**What the evidence says today** (2026-10-03, `origin/main` of both repos): no `InitializeRequest`
type exists; core registers only `FileSystem`, `EmbeddedResource` and `Hub` stream providers and no
GUI attaches a content collection; a missing right on a node create surfaces as `ValidationFailed`;
durable consumers have no checkpoints (only re-query or `_Inbox`); indexing dedupes per
`(collection, file)`, so identical bytes at two paths are indexed twice; no e-mail (`.eml`/`.msg`) or
OCR extraction exists; the Parties labeller was removed from MeshWeaver.Plugins on 2026-09-30 to a
personal home, so it is reusable only once re-homed (G12).

## Related

- [Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes) — the stream design G3 completes.
- [Governed Autonomy](/Doc/Architecture/GovernedAutonomy) — when an action is governed (step 2's park).
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — correctness from node state.
- [Content Indexing Activation](/Doc/Architecture/ContentIndexingActivation) and [Content Chunk Navigation](/Doc/Architecture/ContentChunkNavigation) — the chunk store step 3 writes into.
- [Onboarding a New Environment](/Doc/Architecture/OnboardingNewEnvironment) — a new tenant's storage and its first ingestion.
- Plugins: `Hosting/ActivityExecutionModel`, `Hosting/Queues`, `Governance/Design`, `Testing/EndToEndSpecs`.
- Reinsurance: `Claims/IngestionScenario` — the insurance scenario (#245).
