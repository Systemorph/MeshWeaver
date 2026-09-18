---
Name: MeshWeaver Architecture
Category: Documentation
Description: How the platform works under the hood — message-based communication, the actor model, partitioned persistence, reactive UI streaming, and AI agents
Icon: /static/DocContent/Architecture/icon.svg
---

<div style="background: linear-gradient(135deg, #0d47a1 0%, #1976d2 100%); border-radius: 18px; padding: 40px 34px; margin: 4px 0 30px 0; color: #fff;">
  <div style="font-size: 2.1rem; font-weight: 800; letter-spacing: -0.02em; line-height: 1.15;">Architecture</div>
  <div style="font-size: 1.05rem; opacity: 0.92; margin-top: 10px; max-width: 720px; line-height: 1.55;">
    The backbone of MeshWeaver: message-based communication, the actor model, partitioned persistence, access control, and UI streaming. Start here to understand how the platform works under the hood.
  </div>
</div>

MeshWeaver is a distributed platform for building data-driven applications with AI capabilities. A handful of principles hold the whole system together:

| Principle | What it means |
|---|---|
| **Data locality** | Process and render *where the data lives* — no unnecessary round-trips. |
| **Message-driven** | Every operation is a typed message routed through the hub; no direct object calls across boundaries. |
| **Type as data** | Node types live in the mesh, not only in compiled code — they can be authored, versioned, and released at runtime. |
| **Agent-ready** | AI agents reach everything through the same unified APIs as users — no special back-channels. |
| **Security-first** | Access control is validated at every read and write, not bolted on after the fact. |

## Platform overview

@@content/platform-overview.svg

## Core concepts

<div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; margin: 20px 0;">
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Message-based communication</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Message hubs manage concurrency through the actor model and route messages across the mesh.</div>
    <div style="margin-top: 10px;"><a href="MessageBasedCommunication">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">User interface</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">UI is generated where data lives, serialized to JSON, and streamed to the browser with two-way binding.</div>
    <div style="margin-top: 10px;"><a href="UserInterface">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Agentic AI</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">AI agents are first-class citizens that query the mesh for context and collaborate through messages.</div>
    <div style="margin-top: 10px;"><a href="AgenticAI">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Mesh graph</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Hierarchical namespaces where data types attach at any level, with built-in semantic versioning.</div>
    <div style="margin-top: 10px;"><a href="MeshGraph">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Access control</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Hierarchical, dimensional, and operation-specific permissions enforced on every read and write.</div>
    <div style="margin-top: 10px;"><a href="AccessControl">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Deployment</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Run as a single-process monolith or an Orleans-based distributed mesh orchestrated by .NET Aspire.</div>
    <div style="margin-top: 10px;"><a href="Deployment">Read more →</a></div>
  </div>
</div>

## Topic map

Each theme starts with its introductory page, followed by related architecture topics.

<!-- 🚨 ONE ENTRY PER LINE, and it must stay that way (#3699).
     This map used to be a table whose cells each held the whole entry list on ONE line
     (the longest was 5,877 characters carrying 34 entries). Adding a page meant appending
     to that line, so two doc pull requests conflicted BY CONSTRUCTION however unrelated
     their subjects — and the failure was silent: keeping one side of the merge dropped the
     other side's page from the index, the page stayed reachable by URL, and
     DocumentationLinkIntegrityTest still passed because it checks that links RESOLVE, not
     that pages are LISTED. Appends now land on their own lines and git merges them
     unaided. ArchitectureTopicMapTest asserts both halves: every page under
     Data/Architecture/ is listed here, and no line carries two entries. -->

### Reactive core

- **Start here:** [Asynchronous Calls](AsynchronousCalls) — the no-`await` rulebook
- [Actor Model](ActorModel)
- [Turn-Loop Arrival Order](TurnLoopArrivalOrder) — a hub has TWO queues and a delivery moves between them at turn time, so a turn can be in neither when the last gate opens; the permutation names which message straddled the window, never which defect is live
- [Controlled I/O Pooling](ControlledIoPooling)
- [Subscription Ownership](SubscriptionOwnership) — a pending timer is a GC root
- [Silent Completion](SilentCompletion) — an empty completion is invisible to every timeout
- [AsyncLocal Across Scheduler Hops](AsyncLocalAcrossHops)
- [Initialization Gates](InitializationGates)
- [What the DataContext Init Time-Box Bounds](DataContextInitializationTimeout) — the 120 s box is nested three deep (data source → stream → type-source leg), a per-node hub's initialization is in practice ONE unbounded storage read, and the timeout now names the leg instead of guessing "a stuck NodeType compile", which cannot reach it
- [Retiring an Activation](RetiringAnActivation) — a transient init fault retires instead of latching; the gate is failed BEFORE the dispose, because two drains cannot be ordered by a comment
- [Stale State Until a Recycle](StaleStateUntilRecycle) — an activation serves what it bound and never re-reads it, so merged, sealed, rolled and restarted do not make a fix live at an address that is already up; what a `DisposeRequest` changes and what it provably does not
- [Aggregating Providers](AggregatingProviders)
- [Hub Disposal Model](HubDisposalModel)
- [Transient Node Probes](TransientNodeProbes) — a probe hub's own address is not a node
- [Executive Assistant Credential Reads](ExecutiveAssistantCredentialReads) — a turn that waits for a mesh read holds the queue that read's reply must travel through, and the timeout was rendered as "you never connected"
- [Bounds Must Be Ordered](BoundsMustBeOrdered)
- [Message-Based Communication](MessageBasedCommunication)
- [A Request a Hub Sends to Itself](SelfAddressedRequests) — node CRUD is issued on the hub that executes it, so routing and the reply leg cannot lose it; what that leaves, and why an empty queue at the timeout proves nothing when the handler answers from a detached observable
- [Router Traffic Detection](RouterTrafficDetection) — the detector has two sites; the receiver names the two addresses, the origin names the call site, and a ratchet per tree keeps the seams adopted
- [No Static State](NoStaticState)
- [Observable Hub Pipeline (migration design)](ObservableHubPipeline)
- [Per-Hub TaskScheduler — Actor Isolation Across the Mesh](OrleansTaskScheduler)
- [Removing Hand-Woven Concurrency Gates](RemovingHandWovenGates)
- [Removing Observable-to-Task Bridges](RemovingObservableToTaskBridges)
- [JSON Serialization](Serialization)

### Reading & writing nodes

- **Start here:** [CQRS — Queries vs. Content Access](CqrsAndContentAccess)
- [An Answer Nobody Gave Is Not Cached](AnswerNobodyGaveIsNotCached) — a synced chain replays its FIRST frame for the life of the process, and a provider that completes without an Initial is counted as an empty one, so a cold moment used to become a permanent false "absent"; the frame now names who never answered and an unanswered frame is delivered but not kept
- [MeshNode Stream Cache](MeshNodeStreamCache)
- [Update Queue Ownership](UpdateQueueOwnership) — one published queue per path, retained until accepted work settles
- [Request via Stream Update](RequestViaStreamUpdate)
- [Data Access Patterns](DataAccessPatterns)
- [Node Identity and Path Keying](NodeIdentityAndPathKeying) — `(namespace, id)` is the key and `path` is derived, so splitting a path positionally leaves the path identical while re-keying the node into a second row
- [Workspace References](WorkspaceReferences)
- [Content Chunk Navigation](ContentChunkNavigation)
- [Moving Nodes](MovingNodes) — a move relocates the node and everything that belongs to it, or it refuses
- [Copy Completeness](CopyCompleteness) — a copy asserts a set equality it never established; the two readings that turn a short enumeration from a silence into a number, and why the failure stops instead of rolling back
- [Moved-Node Redirects](NodeRedirects) — keeping links alive after a move
- [MainNode and Rebasing](MainNodeRebasing) — a `with { Namespace = … }` copy un-lists a node with nothing logged
- [Write Verdict Totality](WriteVerdictTotality) — a write whose base read ends empty answers nobody, and arms no deadline either
- [Reading a Base-State Timeout](BaseStateTimeoutCensus) — "no initial state arrived within 30s" has two causes with two different fixes; the census that says which, and how to read one off a running deployment
- [The Phantom Base After Owner Disposal](PhantomBaseAfterOwnerDisposal) — the owner echoes a merge BEFORE it flushes it, so a "never applied" NACK's re-attempt can find its own unpersisted write in the mirror, diff to nothing and report success
- [Conditional Writes Across Hubs](ConditionalWritesAcrossHubs) — on a node you do not own the lambda runs on YOUR mirror and ships a diff, so a field it decided not to write is absent from the patch and a concurrent write to it survives
- [Live Mirrors and the Change Feed](LiveMirrorsAndTheChangeFeed) — a write must not end its own streams
- [Stream Liveness and the Hub Reference](StreamLivenessAndTheHubReference) — a stream outlived the hub it held, and the contract said it could not
- [The sync/ Hub Population](SyncHubPopulation) — a `sync/` hub is one field of a stream and a subscription makes two of them; the only reaper of a Started one is an idle sweep the traffic keeps re-arming
- [A Hub That Pins Its Own Cache Entry](AHubThatPinsItsOwnCacheEntry) — on Orleans every activated hub held the cache's live view of its own path for life, so the entry never released, its heartbeat kept the grain alive, and the loop closed; the one-shot own-node source that opens it
- [The Evicted-Stream Retention](EvictedStreamRetention) — a change-feed eviction parks a remote stream and `ReclaimIfUnheld` refuses to dispose one that carries no lease entry, so every unleased call site retains one stream, and two `sync/` hubs, per change event
- [The Read Path Minted a Hub Per Read](ReadPathStreamMinting) — a live in-process census decomposed a replica's `sync/` hubs into their holders and pinned the growth on the read path: a constant configuration took `GetDataRequest` out of the stream cache, so every read left a permanent hub behind (six reads, six hubs, measured on the running portal)
- [A Reference That Cannot Be a Key](AReferenceThatCannotBeAKey) — the same defect through the other door: a record whose member is a collection is compared BY REFERENCE, so the reference can never hit the stream cache at all; every one of a replica's 311 `sync/` hubs attributed to its minting stream, and the duplicates split into "cache bypassed" and "key unhittable" by comparing the reference objects on the heap. Carries the census script, because the last two were lost
- [The Recursive-Delete Drain](RecursiveDeleteDrain) — the plan is a snapshot the removals may exceed, the completion check must include the ROOT, and the stage bound measures progress, not duration
- [Deleting What Is Already Gone](IdempotentDelete) — an absent node already satisfies the delete's postcondition, so the delete succeeds and reports that it removed nothing; why checking existence first cannot close the race, and which absences are still failures
- [Business Rules & Calculations](BusinessRules)
- [Data Versioning Strategies](DataVersioning)
- [Mesh Graph Architecture](MeshGraph)
- [MeshNode Versioning](MeshNodeVersioning)
- [Query Provider Parity](QueryProviderParity)
- [Search Coverage and Refusal](SearchCoverageAndRefusal) — the `search` tool answered an unanchored query with a clean 0 for nodes it returned when anchored; it now refuses a query that names no partition and declares no fan-out, and every envelope carries `coverage.partitions` — the denominator a zero is read against
- [Query Result Scoring](QueryResultScoring)
- [Reading a Write Verdict](ReadingAWriteVerdict)
- [Satellite Entity Patterns](SatelliteEntityPatterns)
- [Satellite Node Patterns](SatelliteNodePatterns)
- [Repairing a Stale MainNode — When the Broken Field Guards Itself](StaleMainNodeRepair)
- [Synced Mesh Node Queries](SyncedMeshNodeQueries)
- [Update Validators See Typed Content](UpdateValidatorsSeeTypedContent)
- [Content Is Validated Against Its Declared Shape On Write](ContentSchemaOnWrite) — content that cannot bind to its NodeType's declared content type was stored verbatim and then read as absent everywhere; the two shapes the write boundary refuses, the narrow rule that keeps legitimate writers landing, and the wire-boundary residue it does not close
- [The /api/mesh REST Contract](MeshRestApiContract) — a 200 carries the verb's JSON document and nothing else; a sentinel answer is a non-2xx JSON envelope naming the sentence and its kind

### Storage & partitions

- **Start here:** [Postgres Schema Architecture](PostgresSchemaArchitecture)
- [Partition Storage Routing](PartitionStorageRouting)
- [Partition Teardown](PartitionTeardown) — deleting a partition ROOT drops its backing store; keyed on the node's SHAPE, never its NodeType
- [Partition Storage Hubs](PartitionStorageHubs)
- [Partitioned Persistence](PartitionedPersistence)
- [Storage Adapter Implementation](StorageAdapterImplementation)
- [Change-Feed Isolation](ChangeFeedIsolation) — one throwing subscriber must never starve the others
- [In-Memory Child Index Consistency](InMemoryChildIndexConsistency) — a listing taken while the in-memory store re-indexed came back short, a synced query cached it for good, and one NodeType's compile then failed on files that were there; the three rules that keep a reader from ever seeing a half-built index, and how it composes with the mid-install judgement race (#4280)
- [A Container Registry in Memex](ContainerRegistryInMemex) — the fleet's own registry at cr.meshweaver.cloud (a separate distribution + docker_auth service), the bootstrap circularity that keeps the hosting instance's boot image off it, and the in-portal mirror that was built, never wired, and deleted (#4066)
- [Static Repo Import](StaticRepoImport)
- [The Prune Requires a Complete Listing](PruneRequiresACompleteListing) — an import prunes on "absent from the source ⇒ deleted"; a truncated GitHub tree arrives as HTTP 200 and turns every unread file into a deletion
- [Import Write Ordering](ImportWriteOrdering) — a NodeType lands before the instances that name it
- [Import-Side Content Degradation](ImportSideContentDegradation) — a typed file whose own parser is absent on the importing host lands as Markdown with its configuration discarded, and the read-side degradation instrument is blind to it by construction, because the content it wrote types perfectly (#4319)
- [Vector Search](VectorSearch)
- [Durable But Unreadable](DurableButUnreadable) — a write that is acknowledged, versioned and invisible
- [Instance-Key Resolution](InstanceKeyResolution) — the registry reads an instance key through live mirrors, never a per-request point read
- [Cross-Schema Fan-Out Elimination](CrossSchemaFanOutElimination) — an unanchored query is a lock bomb; the census and the per-caller plan
- [Addressed Notifications](AddressedNotifications) — plan 1 worked out: deliver a notification to its addressee so the bell reads two schemas, not 199
- [Content Indexing Activation](ContentIndexingActivation)
- [Cross-Instance Mirror](CrossInstanceMirror)
- [Data Synchronization and CRDT](DataSyncAndCrdt)
- [Setting Up Data Sync](DataSyncSetup)
- [DatabaseBackups](DatabaseBackups)
- [Declarative export and import](DeclarativeImportExport)
- [Syncing a Space with GitHub](GitHubSync)
- [What a Green Build Costs a Synced Space](GitSyncTriggerCost) — one field decided whether a delivery was free or a full clone, and it is deliberately frozen while an import does not converge; the second, weaker pointer that makes a settled source free again, and why the skip needs the verdict to be FINAL and not merely recorded
- [When a Publication Seal Stops Advancing](PublicationSealStarvation) — a Space converges on a green build only while the publication sealed for THIS instance's framework identity keeps reaching the built commit; when the instance's identity and the lane that publishes for it drift apart that condition stops being satisfiable, and until the hold was recorded on the node a held source was byte-identical to a settled one
- [The Import Marker Records Convergence](ImportMarkerRecordsConvergence)
- [A Content Verdict Is Per Node](AContentVerdictIsPerNode) — one unstorable byte in one file earned a partition-wide "these bytes cannot import" verdict and took every instance action offline for an evening; where the refusal is remembered now, and what an import must name when it loses a node
- [A Parked Type Names the Import](AParkedTypeNamesTheImport) — a NodeType failing on `CS0246` for a symbol whose file is plainly in git is an IMPORT state, not a code one; how the compile failure is joined to the import that lost the file, why the join is scoped to the types that actually reference it, and the three answers it keeps apart so an import is never accused of a deletion it did not make
- [Instance Sync — bi-directional space replication between MeshWeaver instances](InstanceSync)
- [Managing Partition Sync (Admin Guide)](PartitionSyncGuide)
- [Static Node Providers](StaticNodeProviders)

### Security

- **Start here:** [Access Control](AccessControl)
- [Granting Access](GrantingAccess)
- [AccessContext Propagation](AccessContextPropagation)
- [Query Identity](QueryIdentity) — an unstamped read answers as Anonymous, which reads as absence
- [Owner Injection](OwnerInjection)
- [Permission API](PermissionApi)
- [Invitation-Only Onboarding](InvitationOnlyOnboarding)
- [Logon Actions](LogonActions) — per-user work at logon, run as the user
- [Unanchored Security Reads](UnanchoredSecurityReads) — why the permission fold reads mesh-wide, and why pinning it to the viewer's partition is a silent revocation-fails-open bug
- [A Denial Is an Answer](DenialIsAnAnswer) — a check on a hub with no evaluator grants Permission.All, and a refusal the mesh decided is rendered, never raised
- [Who Owns a Partition's Access Shape](PartitionAccessOwnership)
- [Partition Ownership Resolution](PartitionOwnershipResolution) — the four create-path checks that ask whether a NodeType owns its partition, what one resolution costs for a type declared in mesh content, which of them share ONE view and which deliberately keeps its own, and how a nested instance of such a type is refused from the definition's durable row without activating the type's hub
- [OWASP ZAP Scan — 3.0.0 (6 September 2026)](SecurityScan_3_0_0)
- [OWASP ZAP Scan — Every Release](SecurityScanning)

### Threads, activities & AI

- **Start here:** [Thread Operations](ThreadOperations)
- [Agent Task Collaboration](AgentTaskCollaboration) — launch shared work only through `start_collaboration`; participant effort, harness, and model are creation-time settings, not follow-up-message overrides
- [Thread Execution Streaming](ThreadExecutionStreaming)
- [Activity Control Plane](ActivityControlPlane)
- [Activity Mirror Release Lifetime](/Doc/Architecture/ActivityMirrorReleaseLifetime)
- [Activity Operations](ActivityOperations)
- [Notifications](Notifications)
- [Notification Retention](NotificationRetention) — the platform's first data-retention pass, and why it is a logon action
- [Agentic AI](AgenticAI)
- [Script Execution](ScriptExecution)
- [Agent Framework Stores — mesh nodes behind Microsoft's abstractions](AgentFrameworkStores)
- [Centralized speech — Whisper Swiss German as a container](CentralizedSpeech)
- [Email Ingestion, Channels and Notifications](EmailIngestionAndNotifications)
- [Event Subscriptions — the durable 'when THIS fires, run THAT' engine](EventSubscriptions)
- [Foreign-Language Bridge (Python, Bun/Node) over gRPC](ForeignLanguageBridge)
- [Foreign-Language & Cross-Platform Integration](ForeignLanguageIntegration)
- [Model Providers and BYO Credentials](ModelProviders)
- [On-device voice — Whisper + Swiss German](OnDeviceVoice)
- [Voice model distribution — where a 547 MB CC BY-NC model may live](VoiceModelDistribution)
- [Python Code Nodes](PythonCodeNodes)
- [Script Execution — Try It](ScriptExecutionDemo)
- [Sending Email](SendingEmail)

### UI

- **Start here:** [User Interface](UserInterface)
- [Blazor Data Binding](BlazorDataBinding)
- [Per-Tab Session State](PerTabSessionState) — a node is shared by every tab of one account, so "which page is this viewer on" and "navigate ME there" can never live on one
- [Blazor Async](BlazorAsync)
- [Available Controls](UserInterface/AvailableControls)
- [Node Page Provenance](NodePageProvenance) — the landing page carries "who wrote this, and when" by default; declining it is a sentence, not an absence
- [Menus as Data](MenuAsData) — re-word a menu without a build
- [The Menu Contribution Boundary](MenuContributionBoundary) — what may be data, what stays compiled
- [Markdown Fence Extensions](MarkdownFenceExtensions) — the platform emits a marker, the clients hydrate it; a new fence is always a two-repo change
- [Localization](Localization) — the viewer's language, resolved explicitly, never from ambient culture
- [Chrome and Content Language](ChromeAndContentLanguage) — ownership decides the language, and in-flow chrome minimises words
- [Localized Refusals](LocalizedRefusals) — a `*Response.Error` is a wire field and stays English; the activity transcript is the surface a viewer reads
- [The Supplied Navigation Rail](SuppliedNavigationRail) — a module supplies its own left-hand index, and core renders it in the order the module gave
- [The Apps Home](AppsHome)
- [Content Favicon Rasterization](ContentFaviconRasterization)
- [Controls That Cannot Fail](ControlsThatCannotFail)
- [Catalog Action Identity](CatalogActionIdentity) — a retained click keeps its package when the catalog refreshes
- [Link Previews](LinkPreviews)
- [Public Web Presence](PublicWebPresence) — one public host, a body in the first response, a sitemap that descends to every page a stranger may open
- [Local-First Client & Bootstrap](LocalFirstClient)
- [PDF Export — one browser, two fidelities](PixelFaithfulExport)
- [UI Extensibility](UiExtensibility)

### Node types

- **Start here:** [Adding a New Node Type](AddingANewNodeType)
- [Creatable Types](CreatableTypes) — what may be created under a node: the one provider the Create form asks, what a parent NodeType restricts, what it extends, and why a parent that declares nothing must never narrow the menu
- [Retiring a NodeType](RetiringANodeType) — the prune keeps the definition and deletes its sources
- [Dangling NodeTypes](DanglingNodeTypes) — a node whose type resolves to nothing, and the two write paths that allowed it
- [Node Type Compilation](NodeTypeCompilation)
- [Who Owns a NodeType Member](NodeTypeMemberOwnership) — the repo owns the definition, the mesh owns the compile state, and the mask that encodes it was pinned in ONE direction: four runtime-state members were missing from it, one spelled outside the naming convention meant to catch them. The three guards, and why a member the mesh writes may still have to stay unmasked
- [Compile Cache Input Freshness](CompileCacheInputFreshness) — verify the captured input before reusing a DLL that finished after a source edit
- [Execute-Time Interlock](ExecuteTimeInterlock) — a build proven stale is never armed
- [Emit Reference Capture](EmitReferenceCapture) — bounded, opt-in CI evidence for runtime compiler failures
- [Graph / Compiler Layering](GraphCompilerLayering) — the four assemblies, the cycle, and the full-MVID size rule
- [Toolchain Re-evaluation Lane](ToolchainReevaluationLane) — why a toolchain change stopped rebaking the world
- [The Dependency Record Floor](DependencyRecordFloor) — a record's module entry says "I need at least X", not "I need exactly this build"; the MVID pin that could not converge because Roslyn hashes absolute source paths, the two-replica recompile ping-pong it produced, and the four things the floor deliberately does not relax
- [An Unloadable Build Is Never A Silent Default](AnUnloadableBuildIsNeverASilentDefault) — a recorded build that does not LOAD in this process used to bind the mesh default configuration for the grain's whole life; the always-activated Hosting/PlatformBuilds hub that ran twenty hours without its inbox, fleet watch and build queue while its record read Ok, and the two hypotheses (a missing module, "a restart activates it") the measurements refuted
- [Producer Determinism of the Dependency Record](ProducerDeterminismOfTheDependencyRecord) — the same content must stamp the same record however the producer reached its bytes; the disk-cache hit that shipped a weaker guard, and why the digest is persisted beside the bytes rather than recomputed
- [Rebake Waves](RebakeWaves) — why a roll rebakes the world anyway, and what one rebake writes
- [Source-Set Establishment](SourceSetEstablishment) — a resolved source set of ZERO is ambiguous, and only the type's own persisted snapshot tells "owns no Code" from "the discovery pass came back short"; the boot that resolved 91 fewer Code nodes than its neighbours and held a portal out of rotation for the startup probe's full three hours
- [Missing Declared Sources](MissingDeclaredSources) — emptiness measured on the UNION is invisible for any type that also draws on a shared library; the NodeType whose own Source subtree was gone, reported itself as broken C#, and had the identical doomed compile taken again on every boot for four days
- [Compiled Against A Platform The Instance Does Not Run](CompiledAgainstAnotherPlatform) — the source compile gate ran and PASSED: it compiles against the platform CI is about to ship, while the receiving instance is still on an older image. Six NodeTypes on memex-cloud stopped compiling forty minutes after that satellite merge; why the prebuilt was declined and Roslyn ran, and why the `CS1061` localizes the fault to the platform surface without saying which side moved
- [Install-Time Prebuilt Adoption](InstallTimePrebuiltAdoption) — the only lane that serves a package installed AFTER boot, and the four answers its zero must keep apart because a silent non-adoption reads exactly like a successful one
- [Adoption and the Sweep Count Different Things](AdoptionAndTheSweepCountDifferentThings) — the cold boot that adopted 78 prebuilt assemblies and then reported 5, with nothing wrong on the share: what each instrument counts, why the sweep could not see its own process's writes, and the node-version ordering that keeps the fix from becoming a stale serve
- [A Census That Counts Must Name](ACensusThatCountsMustName) — the one past-RLS census counted a permanently-broken NodeType and dropped its path one call before publication, so its output read as clean; where the identity was lost, what a PUBLIC census may name (the partition, never the node title), and how to tell a fix that is merged from a fix that is running
- [Import Write Ordering](ImportWriteOrdering) — type before instance, and what a foreign type does
- [Language Services](LanguageServices)
- [Extensible Defaults](ExtensibleDefaults)
- [Build coordination — the Build node protocol](BuildCoordination)
- [Build Identity Admission](BuildIdentityAdmission)
- [The Build Process — compile and test as a dependency cascade](BuildProcess)
- [What a Pull Request Rebuilds](BuildScopeNarrowing)
- [The Build Server](BuildServer)
- [The Compile Program — State of Record](CompileProgramStateOfRecord)
- [Content-Type Registration](ContentTypeRegistration)
- [Import-Side Content Degradation](ImportSideContentDegradation) — the OTHER half: content degraded at IMPORT rather than at read. The `/health` `content-types` census cannot see it, because an import-degraded node holds a plain `MarkdownContent` whose `$type` resolves perfectly on every replica
- [NodeType Catalogs (shipping instances of a NodeType)](NodeTypeCatalogs)
- [NodeType Release Redesign](Postmortems/NodeTypeReleaseRedesign)

### Plugins & content delivery

- **Start here:** [Plugins](Plugins) — node repos from git, no NuGet
- [Plugin Manual](PluginAuthoring) — author · publish · install · own registry
- [Plugin Registry](PluginRegistry) — memex re-serves plugins over REST
- [Webhook Inbox](WebhookInbox) — external services deliver into {target}/_Inbox
- [Plugin Packaging](PluginPackaging) — bundles, the framework identity, and the `Release` node that links a release to its assemblies per architecture
- [Install Readability](InstallReadability) — the two doors an install can open, and the cover-grant deadlock detector
- [A Module's Static Web Assets](ModuleStaticAssets) — a module's CSS/JS ride the bundle in their own folder and must land MODULE-RELATIVE beside the entry assembly; anything that copies only the closure loads perfectly and 404s every asset behind one Debug line
- [Static Repo Import](StaticRepoImport)
- [Adopt Then Sync, Per NodeType](AdoptThenSyncPerNodeType) — the seal is a REPOSITORY fact and adoption is a per-NodeType one, so landing a Space on the sealed commit is not enough: an adopted type's sources wait for the bundle built from them, the rest of the Space imports, and a partially-held Space keeps the commit it genuinely holds
- [The Sync-Ref Contract](SyncRefContract) — an import of a repository whose bundles this instance runs lands on the commit they were baked from, whoever asked — a person's Update included since 2026-09-17; every other repository reads a commit CI proved, or a branch tip a person asked for; resolving the ref twice put sources no build had compiled onto two production portals for five hours
- [Node Type Compilation](NodeTypeCompilation)
- [The Platform Image's Closure](PlatformImageClosure) — the image IS the reference set every satellite's modules compile against; the two invariants, and why every consumer used to discover them by failing to compile
- [Compiled Against A Platform The Instance Does Not Run](CompiledAgainstAnotherPlatform) — the link gate measures a module's bytes against the platform ACTUALLY RUNNING; the source lane measures Code nodes against the platform it is about to ship, and `judge-against-baseline` — the arm that reads what the fleet runs today — is wired into core's promote, not into the satellite PR where the content half lands first
- [Sealed Publication Reads](SealedPublicationReads) — a publication is unreadable for ~90s per target per publish and the `plugins` prefix has two writers; the three answers (404/503/412), the generation that pins one instance, and the mix no reader can detect
- [Sealed Publication Generations](SealedPublicationGenerations) — the layout that makes that mix UNREPRESENTABLE (a directory per publication plus a pointer swapped last), the reader contract, the retention rule, and the ordered migration that avoids a new-writer/old-writer half-state
- [Install Completeness](InstallCompleteness) — what an install RECORD declares landed, compared against what is actually in the mesh; the comparison nothing made until #3485, and why only one of its five verdicts is a pass
- [Declared Is Not Landed](DeclaredIsNotLanded) — the three exits an install can take and the one that never looked at the mesh: an UPDATE fetched the manifest diff alone, so a node lost after a previous install survived every subsequent update, each reporting success. The measured case, the batched presence read that fixes it, and the sweep hazard that made the damage read smaller than it was
- [One Partition, One Bookkeeping](OnePartitionOneBookkeeping) — a partition written by BOTH a GitSync source and the registry installer keeps two independent records of one mesh; the seal reconcile rewrites the content and the next registry delta is computed against a record that stopped describing it, so `Store` on memex became a mix of 1.10.3 and 1.11.1 and `Store/Catalog` parked on `CS1061`. The invariant, why resetting the record or diffing the mesh both produce a ping-pong, and the two gates
- [CI Content Bake](CiContentBake)
- [The In-Mesh Warning Standard](InMeshWarningStandard) — in-mesh C# is the only C# no `-warnaserror` build ever sees, and the bake was discarding its warnings too; the two shrink-only ratchets (real warnings, and CS1591 on its own), why the RUNTIME compile must stay lenient — a parked NodeType refuses readiness and stalls a rollout — the three codes the platform itself was emitting into content it does not own (850 raw occurrences → 375), and the observe-only default that lets a repo adopt without going red
- [Bundle Delivery Stages](BundleDeliveryStages) — the four independent stages between a merge and a portal serving prebuilt bytes (write · compose · select · deliver), which of #3461 / #3732 / #3768 / #3583 owns each, the instrument that answers for each — and why a reading taken at one stage is not evidence about another
- [Framework Identity Churn](FrameworkIdentityChurn) — the identity moves on every core COMMIT, not on every content change (43 merges, 5 touching the full-MVID set, ≥18 identities in 24h); the commit sha compiled into `AssemblyInformationalVersion` is why, the falsification test that refuted the local fix (0 of 22 control, 22 of 22 and 21 of 21), and the four costed options with every runner-hour labelled as arithmetic
- [Prebuilt Bundle Retention](PrebuiltBundleRetention) — the sweep that prunes what CI bakes: where it is registered (and why "zero callers" was measured twice and wrong both times), the deletion default that is `true` in code and `false` in the chart, the report that names its denominator, and the pinned satellite gate the protected set cannot see
- [Deploying a plugin change — merging is not shipping](DeployingPluginChanges)
- [Module Activation Head Ownership](ModuleActivationHeadOwnership) — which generation a deployment runs is a decision every replica writes to ONE shared file and replaces unconditionally; why the same-content case is already benign, why the regression propagates into the proposed module set rather than being bounded by it, why routing the write to an owning hub is a cycle (boot reads the record before the mesh exists), and what deriving the head costs measured rather than estimated
- [Module Adoption Policy](ModuleAdoptionPolicy)
- [Publishing A File On A Shared Volume](AtomicFilePublication) — a name readers watch must appear holding the whole file or not appear at all, and `File.Move(…, overwrite: false)` does not promise that: on a volume without hard links (Azure Files) its failed rename COPIES into the final name, which is then incomplete and exclusively locked for the length of the copy (measured: 21,573 sharing violations, 52,619 incomplete reads, ZERO "absent"). The primitive that replaces it, what it refuses rather than copies, and what is still not atomic
- [Module Build Architecture](ModuleBuildArchitecture)
- [Module Closure Accounting](ModuleClosureAccounting)
- [The Module Identity Anchor](ModuleIdentityAnchor)
- [Two Identity Schemes, One Comparison](ModuleIdentitySchemes) — a bundle states a `g<sha>` commit identity and a portal resolves an `s<hash>` surface identity, so the #4161 discriminator answered "different" for every pair in the fleet: measured on memex.systemorph.com, eight modules declined on every boot and reported as "a restart activates them" across a restart that could not clear one of them
- [Module-Owned Siblings Ride](ModuleOwnedSiblingsRide)
- [The Module Platform Link Gate](ModulePlatformLinkGate)
- [Rolling-Update Build Tolerance](RollingUpdateBuildTolerance) — why a rolling platform update recompiled Store/Plugin ten times in four minutes and blanked every instance behind "build did not settle within 30s" (two generations, one record, one framework identity per record), and the rules that let an instance render on the last build its process can load instead
- [The Module Publication Gate](ModulePublicationGate) — a bundle used to reach the live registry from inside its own pack leg, before the sibling suites, the portal-host shards, the compile-check and the Tests-area gate had reported; the hand-over moved downstream of the full source verdict, and what it refuses (failed, skipped, cancelled, missing, foreign-lane, substituted)
- [Module Generation Substitution](ModuleGenerationSubstitution) — `Assembly.LoadFrom` does not promise to load the path it is handed: a byte-identical copy the load context already holds is returned instead, silently, so the loader recorded the generation it ASKED for while the process ran another; the three answers that replace two
- [Module Set Convergence](ModuleSetConvergence)
- [Module Versioning](ModuleVersioning)
- [Modules](Modules)
- [Required Module Authority](RequiredModuleAuthority) — `Modules:Required` is an array and configuration merges arrays BY INDEX, so a record's list is an overlay and not a statement: a shorter list leaves the image's tail required and an EMPTY list requires MORE; the scalar claim that lets a record say "these and only these", why it is opt-in, and the two instruments that make the gap visible
- [Package Mark Inheritance](PackageMarkInheritance)
- [Pin-Boundary Contracts](PinBoundaryContracts)
- [Platform and content — two layers, two cadences](PlatformAndContent)
- [Platform Build Identity](PlatformBuildIdentity)
- [The Platform-Shipped Witness](PlatformShippedWitness)
- [The Plugin Build Contract](PluginBuildContract)
- [Plugin Bundles in the Registry](PluginBundlesInTheRegistry)
- [The Registry Listing Cache](RegistryListingCache) — GET /api/plugins re-read the whole repository per request and blew its 30 s budget ~60×/day; what is cached is the SOURCE snapshot, never the response, which is what keeps the fix from becoming a disclosure — and the read itself now transfers only the manifests it parses (47.8 MB / 13 s becomes 1.3 MB / 3.3 s) instead of the whole repository
- [The Bundle Transfer Budget](BundleTransferBudget) — a module adopt's 120 s attempt used to cover the whole archive download, so the budget measured size ÷ throughput instead of whether the registry was answering; 18 adopts failed at exactly the outer 3-minute bound recording neither bytes nor elapsed time, which is why they could not be explained
- [Plugin Publication Provenance](PluginPublicationProvenance) — the signed publication callback names the CONTENT commit that was built and the platform version read from the selected portal image, never the calling workflow's commit or event; core CD building Plugins used to announce a core sha as a Plugins commit
- [Plugin Update on Green Build](PluginUpdateOnGreenBuild)

### Reliability & wedges

- **Start here:** [Error Propagation & Wedges](ErrorPropagationAndWedges) — drive wedges to 0
- [Which Kind of Silence](WhichKindOfSilence) — a quiet log has four causes with four owners; the liveness heartbeat that separates them, and the CPU sample and page snapshot that agreed with the wrong one
- [Action-Block Wedge Prevention](ActionBlockWedgePrevention)
- [Riding Out a ShuttingDown Address](RidingOutAShuttingDownAddress) — the one transient NACK, and the two axes a ride-out must bound separately
- [Hub Initialization Failure](HubInitializationFailure)
- [Orleans Stream Pub-Sub Durability](OrleansStreamPubSubDurability) — a publish with no subscriber succeeds, so a cross-silo reply can vanish with nothing logged
- [Durable Streams Are Mesh Nodes](DurableStreamsViaMeshNodes) — the design that retires the memory stream without a provider
- [The Pod-Hub Claim Must Be Re-Asserted](PodHubClaimReassertion) — a claim asserted once into a directory that is re-partitioned on every membership change is lost silently, and forever
- [Oversized Delivery Refusal](OversizedDeliveryRefusal) — a message too large for its transport destroys the connection carrying it; refuse at the producer, never raise the limit
- [Content Sync Visibility](ContentSyncVisibility) — a Space whose assets the transport refuses says so, on the Space itself, naming the file, its size and the limit
- [Out-of-Band Content Transfer](OutOfBandContentTransfer) — a content file too large for one delivery travels through the content store behind a content-addressed handle, never on the message
- [An Unreachable Store Is Not a Refusal](StoreUnreachableIsNotARefusal) — one classification, three consumers; reporting an availability failure as a verdict is how a retried create becomes a duplicate
- [A Name That Does Not Resolve Is Not Transient](ANameThatDoesNotResolveIsNotTransient) — the default pipeline retried a hostname that does not exist three times and logged each attempt at Error, so a URL in somebody's data manufactured a platform incident; the one socket error that is permanent, why the breaker must not count it either, and the control that keeps a nameserver hiccup retryable
- [A Bulk Create Compensates Per Node](BulkCreateCompensation) — every row is durable before any post-creation handler runs, so one critical failure left the failed node AND every node after it, whose handlers never ran and which nothing can tell apart from a success; what the rollback removes, why it walks backwards, and the measured reason the stop is a fault and not a `Take(1)`
- [Undetermined Is Not No](UndeterminedIsNotNo) — a read that did not answer is a THIRD state; the second door that shared the first door's failure domain, and the rule for what a gate does with "I could not determine"
- [Reading a Silo Eviction](ReadingASiloEviction) — a heartbeat newer than the suspect votes is not proof the silo was healthy; the control arm that tells a correct eviction from a false positive
- [Dead-Circuit Fan-Out Storm](DeadCircuitFanOutStorm) — a closed tab's owner pushed to the corpse for 46 minutes because the only verdict the eviction acts on could not be said; the release tombstone that says it
- [Bake Seal — NodeOps Saturation](BakeSealNodeOpsSaturation) — the mesh's ONE node-CRUD hub stops draining under a bulk burst, and every consumer then reports its own bound
- [The /api/content 503](ContentRoute503) — three causes with different fixes, why the third wears the first's signature, and the black-box discriminator that needs no log line
- [Refused Replies During Teardown](RefusedRepliesDuringTeardown) — every failure route answers the SENDER, which for a reply is the responder; the answer the caller is parked on is dropped with nobody told
- [Refusing a Lost User Action](RefusingALostUserAction) — a click whose stream is gone is refused out loud instead of dropped as churn; why "deliver it anyway" is not implementable as stated
- [Guards and Unknown States](GuardsAndUnknownStates)
- [Mesh Admission](MeshAdmission)
- [Mesh Lifecycle — Build Up & Tear Down](MeshLifecycle)
- [Pod-Hub Delivery — the Transport Swap and its Roll Plan](PodHubDeliveryRollPlan)
- [The Portal Heap Is Hubs](PortalHeapIsHubs)
- [SignalR Mesh Participant — joining the mesh over a WebSocket](SignalRMeshParticipant)
- [Teardown Layers — work finishes, nothing is forced](TeardownLayers)
- [Teardown Verdicts Are Causal, Not Timed](TeardownVerdictsAreCausal)

### Testing & debugging

- **Start here:** [Writing Tests](WritingTests)
- [Negative Controls](NegativeControls) — a pin is only a pin if it fails against the defect
- [Reactive Test Assertions](ReactiveTestAssertions)
- [Test State Isolation](TestStateIsolation)
- [Disposable-mesh e2e](DisposableMeshE2E)
- [Debugging Message Flow](DebuggingMessageFlow)
- [Debugging Disposal & Leaks](DebuggingDisposalAndLeaks)
- [Departed Platform Assemblies](DepartedPlatformAssemblies) — an assembly that leaves the platform for a module breaks every OTHER module that binds it, at LOAD time and invisibly to every compile gate; why "those are the platform" is one answer per host
- [Detached Response Continuations](DetachedResponseContinuations) — why a `hub.Observe(...)` continuation runs on the RESPONDING hub's action block, what that cost on the mesh's one node-CRUD hub, and the six invariants that make the hop an opt-in rather than the default
- [Reading a Disposal Stall Verdict](DisposalStallVerdicts) — what each field of the disposal snapshot actually measures, the three that were read as evidence while measuring nothing, and the verdict hole that sent 47 reports to children that were not the problem
- [A Failure Report Answers Its Own Instruction](AFailureReportAnswersItsOwnInstruction) — a report that says "find why this hub disposed" while holding the answer, and an outstanding-work field that rendered "not measured" identically to "none"
- [Ambient Test-Host Hangs](AmbientTestHostHangs) — what decides whether a killed test host can be diagnosed at all, and the readings of it already falsified
- [In-Mesh Tests and the Seal](InMeshTestsAndTheSeal) — a Tests area no required context executes is a latent trunk red the seal detonates fleet-wide; how to measure a gate before requiring it
- [Cancel and Join Are Two Questions](CancelAndJoinSequencing) — a deadline that asks work to stop and a deadline that waits for it to have stopped must not share one clock
- [Collection-Scoped Test Fixtures](CollectionScopedTestFixtures)
- [Debugging Native Crashes (core dumps)](DebuggingNativeCrashes)
- [Reading the Memory Watchdog](ReadingTheMemoryWatchdog) — a step with no mesh class active is a plain test class, a ramp across mesh classes is retention; the guard states what it measured, never a cause
- [Debugging Postgres in Prod / Test](DebuggingPostgres)
- [Decentralised Tests](DecentralisedTests)
- [Gate Content Assets](GateContentAssets)
- [In-Mesh Build and Test](InMeshBuildAndTest)
- [Orleans Test Routing Pattern](OrleansTestRoutingPattern)
- [Reading CI Signals](ReadingCiSignals)
- [Which Attempt an Artefact Belongs To](ArtifactAttemptAttribution) — a run holds every attempt's artefacts and the API names no attempt; how the required check consolidated attempt 1 over attempt 2's green and could not be re-run to green
- [Workflow Permission Pairing](WorkflowPermissionPairing) — a job-level `permissions:` in a shared lane is a requirement on every caller; an unpaired one is `startup_failure` with zero jobs

### Deployment & ops

- **Start here:** [Deployment](Deployment) (the router)
- [AKS](DeploymentAKS)
- [Database Migration Procedure](DatabaseMigrationProcedure) — the schema moves before the image, every roll; the 2026-09-03 wedge behind a 200, the recovery, and why a migration deadlocks under load
- [Container Apps](DeploymentContainerApps)
- [Local Dev Workflow](LocalDevWorkflow)
- [Onboarding a New Environment](OnboardingNewEnvironment)
- [Unclaimed Control-Plane Requests](UnclaimedControlPlaneRequests) — an InstanceAction at version 1 with an empty log means "queued", "nobody is listening" and "the operator died holding it" in the same bytes; the 2026-09-10 measurement, the `Ops/Status` staleness that DOES discriminate, and the acceptance signal that does not exist
- [Release & Self-Update Strategy](ReleaseStrategy)
- [Release Support Policy](/Doc/Architecture/SupportPolicy)
- [Released Artifact Retention](ReleasedArtifactRetention) — retain artifacts for at least 30 days, supported releases for their support lifetime, and every artifact still needed by a published set or consumer
- [Self-Update Target Selection](SelfUpdateTargetSelection) — candidates are ranked by the CD run number, not the version string; a mislabelled line outranked every sealed set for ever, and an install on a withdrawn tag could never see anything newer
- [The Self-Update Registry Credential](SelfUpdateRegistryCredential) — which plugin-registry key may be presented to a container registry: a DECLARED pairing, never host equality or name resemblance; an absent declaration refuses
- [Self-Update on the Control Lane](SelfUpdateControlLane) — detection stays on the instance, the apply is one signed event to the control instance, the chart's one declaration binds the self-patch Role to the poller's intent; no portal holds a credential that changes the cluster
- [The Continuous Delivery Contract](ContinuousDeliveryContract) — all-or-nothing publication; verify the image, never the tick
- [Reading a Bake Publication Receipt](BakePublicationReceipt) — the four target outcomes and what each licenses; the one that had no word rendered "already everywhere" as "reached nothing", and two readers acted on it
- [CD Reconciles the Plugins Seal](CdReconcilesThePluginsSeal) — a set seals on its trio alone, so it can seal with no `plugins` publication for its framework identity; why the reconciler REPAIRS that rather than the seal forbidding it, and the three probe answers of which only one licenses a re-attempt
- [The Self-Update Schema Wall](SelfUpdateSchemaWall) — every schema-bumping release is un-takeable by self-update, the stall is invisible, and a promoted tag is not a deployable tag
- [Bake Identity Mismatch](BakeIdentityMismatch) — why a green CD can publish a bake no portal adopts, and the one rule that keeps two images of one commit on one address
- [Release Availability Gates](ReleaseGates) — one predicate; never roll or build into a release a package cannot survive
- [Combo Gate Wiring](ComboGateWiring) — the roll consults the combo verdict; Red refuses, and "could not find out" is neither
- [Roll Selection](RollSelection) — completeness as a SELECTION criterion: pick the latest release that ships all of an environment's plugins, refuse an empty denominator, and never roll backwards
- [Release Process](ReleaseProcess)
- [NuGet Package Retirement](NuGetPackageRetirement) — two packages survive (the Aspire integration and the `dotnet new` template) and the other forty-three are unlisted; what unlisting does and does not break, the derived retirement sweep, and the ground rule that startup dependencies become Aspire options rather than new packages
- [Repository Dependency Direction](RepositoryDependencyDirection) — the platform never depends on a plugin repo; the inventory of every edge that still does
- [The Cross-Repo Pair Gate](CrossRepoPairGate) — a removal here reds a plugin repo's trunk hours later; the deleting half lands LAST
- [Platform Script Resolution](PlatformScriptResolution) — a repo runs the platform's gate scripts, never a copy; the local runner must resolve the ref the LANE resolves, which is per-script, so a loader copied from another repo refuses on every call (one repo demands one lane sha, another pins four and the pin gate calls that consistent)
- [Keeping the Platform Source Pin Current](PlatformRefBumpLane) — a satellite pins WHICH core commit its `src/` compiles against, the image set had a mover and the source ref had none, and a bump PR opened with `GITHUB_TOKEN` is a PR no CI ever runs
- [Transitional Allow Entries](TransitionalAllowEntries) — an allow entry is written for ONE merge and expires with it by mechanism; the instruction that was ignored once cost every C#-touching PR in the fleet ~40 minutes of red
- [Pinned Image Retention](PinnedImageRetention) — registry retention deletes what CI pins, and republishing frequency is what destroys a pin rather than what protects it; the guard that names a dead pin, and the retention design that stops the deletion
- [Artifact Retention Interlock](ArtifactRetentionInterlock) — the one mechanism behind the four retention issues: cleanup may delete only what a COMPLETE and FRESH consumer inventory shows to be unreferenced. Three axes (the third asks each installation what it is RUNNING, because a committed pin is a proxy that drifts), the denominator every run must state, the TAG lock a manifest lock does not provide, and the instrument control that replaced an assertion about the fleet
- [CI Artifact Storage](CiArtifactStorage) — where CI's big build outputs live: the measured $260/month GitHub Actions storage bill and the org budget that decides whether a private repo can upload at all, the ONE artifact family that is read across runs (and why most of its bytes are duplicates of themselves), the 66.8 GB uploaded for a reader that was never built — and the stale checkout that made "nothing reads this" wrong, why an EXPIRED artifact is still billed and still deletable, the object-store seam and the degrade rule that keeps the public repo working, and the grants and the one variable the migration still needs
- [PR Artifacts on Our Infrastructure](OwnPrArtifacts) — moving PR compute to our runners did not move its artifact BYTES: the named-artifact transport (`store:` on the shared upload/download actions, a declared store that cannot be used being an error and never a quiet fall back to GitHub), the partial-rerun selection rule, and the four rollout gates that separate "the transport exists" from "a private PR uses it" — including why queue admission is not a native build worker
- [Fleet Registry Retention](FleetRegistryRetention) — the same question asked of `cr.meshweaver.cloud`, the fleet's OWN registry and the default for new instances: what deletes today (nothing — enumerated, with the one row that is a maintainer read), and why a registry with NO LOCK needs a stricter rule than the ACR rather than the same one, because there the derivation IS the whole safety margin
- [The Image Tag Contract](ImageTagContract) — which image tags the promotion actually publishes, why the portal has no `latest`, and the two-writer history of the one that had no producer at all: retired lane, then retention, and every check green throughout
- [Pin Set Consistency](PinSetConsistency) — every pinned digest EXISTING is not every pinned digest naming the same BUILD; the invariants that red a half-moved set, three written deliberately weaker than the obvious version, and the falsification that found the vacuity trap inside the gate itself
- [Duplicate Keys in Workflow YAML](WorkflowDuplicateKeys) — a duplicate mapping key is accepted silently and the LAST one wins, so a pin can move in the diff and not in the job; the near-miss, why every existing gate was blind, and the guard that names the file, the key and both lines at the first job
- [Image Pair Skew](ImagePairSkew) — a promoted image pairs a core commit with a Plugins head resolved hours later; each half green, the pair never run (the 2026-09-03 sign-in outage)
- [The Merge Queue](MergeQueue) — one entry built at a time so nothing churns, and a steward that re-queues an ejected PR on evidence and never re-runs
- [Review Findings Answered](ReviewFindingsAnswered) — a pull request reads red until the automatic review has landed and each thread it opened has a person's reply; the reviewer's two logins, the quota refusal posted as a review, and the maintainer-only waiver
- [Carving Projects Out Of Core](CarvingProjectsOutOfCore) — what a SOURCE move costs and what it does not
- [Red-Log Watching & Ticketing](LogWatchTriage) — every `fail:`/`crit:` becomes exactly one triaged issue
- [Log Entries Are a Query Result, Not a Feed](LogEntriesAreAQueryResult) — `Hosting/LogEntry` is the output of one `Logs` action, so an absence in it is evidence of nothing; the denominator printed on every row, the level that lives on a different node, and how to ask for a line that carries an answer
- [Verifying Chart Values](VerifyingChartValues) — a key can be set, reach the render, and still not be read; why the obvious gate was vacuous for the one component it existed to guard, and the binary check that closes it (the drain that erased every namespace's log history)
- [Measuring a Live Portal Read-Only](MeasuringALivePortalReadOnly) — `/health` first (public, past RLS, a different replica each call), the incident store, the four break-glass instruments, and why an absence needs a coverage fact before it counts as evidence
- [Chart Ownership and the Runner Pool](ChartOwnershipAndRunnerPool) — why the chart's gate is here, what a relocation must carry, and why path-filtering it is unsafe
- [Sharding the Node-Repo Gate](NodeRepoGateSharding) — a cap cut reports as `cancelled`, so the fan-out that removes it, and the fold that keeps ONE required context and ONE gate log
- [Applying Is Not Rolling Out](ApplyingIsNotRollingOut) — helm applies, the caller observes; the fixed fifteen-minute `--atomic --wait` that reverted a correct upgrade mid-startup-gate, and why a bigger timeout only moves the cliff
- [Probe Semantics](ProbeSemantics) — readiness, liveness and startup ask three different questions with three different remedies; why they get three paths and three tags
- [A Probe Must Answer Inside Its Own Timeout](AProbeMustAnswerInsideItsOwnTimeout) — `/health` reached 8-10 s against the 5 s the startup probe waits, so a healthy replica could never leave startup and was killed at its 3 h budget; why a startup timeout is the unrecoverable one, why a HEALTHY slow check is the one nothing could name, and the timing line the endpoint now publishes
- [What a Synthetic Probe May Assert](SyntheticProbeTargets) — a probe naming one deployment's installed content is broken by construction; the platform floor, the negative control that tells "absent" from "down", and reading the target's own declaration
- [Why a GC-Bound Pod Stays in Rotation](WhyAGcBoundPodStaysInRotation) — the GC's hard limit sits below the container limit, so a portal short of memory is defended rather than restarted
- [Self-hosted CI runners on AKS](SelfHostedRunners) — ARC beside the portals on one pool; three brakes, a negative priority class, and the reserve arithmetic that decides the cap
- [Candidate Release Protocol](CandidateReleaseProtocol)
- [Chart Drift — what a deploy actually does](ChartDriftSemantics)
- [Rendering a chart you are not allowed to fully configure](ChartDriftRenderWithoutSecrets) — 39 runs, 39 failures, zero verdicts: the check may hold two of the deploy's three value sources and the chart correctly refuses that subset; the placeholder that unblocks the render, the two-render proof that no compared object depends on it, and the bake gate that was off on both production namespaces the moment a verdict finally appeared
- [Configuring an instance from Aspire](ConfiguringAnInstanceFromAspire)
- [The Dependabot Secret Store](DependabotSecretStore)
- [GitHub App Credentials](GitHubAppCredentials) — `meshweaver-cloud` writes to its own repo; every cross-repo READ mints from the read-only `fleet-reader`
- [Deployment env layers — what a record must be able to hold](DeploymentEnvLayers)
- [DeploymentInventory](DeploymentInventory)
- [Deployment Options (AKS)](DeploymentOptions)
- [Environment Composition](EnvironmentComposition)
- [Feature Flags](FeatureFlags)
- [First-Run Setup](FirstRunSetup)
- [Image Cleanup](ImageCleanup)
- [Instance Identity and Setup](InstanceIdentityAndSetup)
- [Instance Lifecycle — State of Record](InstanceLifecycleStateOfRecord)
- [Instances](Instances)
- [Local memex on Colima k3s (Mac)](LocalColimaMac)
- [Mac local stack — on-device AI + local observability (M-series)](MacLocalStack)
- [Memex Cloud Deployment](MemexCloudDeployment)
- [Merge Queue Mechanics](MergeQueueMechanics)
- [The fleet's ONE CI process](FleetCiProcess) — the mechanism lives here as a lane or a shared script; a satellite carries a thin caller (verdict adoption, the lock resolver, the cancellation rule)
- [Operating from the portal, not the cluster](OperatingFromThePortal)
- [The Payment Provider Contract](PaymentProviderContract)
- [Pre-Boot Service Substitution](PreBootServiceSubstitution)
- [Project Templates](ProjectTemplates)
- [Reading a Recurrence Reopen](ReadingARecurrenceReopen) — a bot reopen asserts two things and both fail independently; the 2026-09-17 wave measured, and the honest triple behind core's count
- [Reopening on Image Provenance](ReopeningOnImageProvenance) — the third reopen predicate: an occurrence counts only if it came from an image whose commit contains the fix, and why a staleness window would close live defects
- [Registry-key rotation — two phases, at the registry that holds the instance](RegistryKeyRotation)
- [The Release Event Bus](ReleaseEventBus)
- [The Release Gate's Denominator](ReleaseGateDenominator)
- [Release to Production — the whole path](ReleaseToProductionPipeline)
- [Renaming a Required Status Check](RenamingARequiredCheck)
- [Repository Topology](RepositoryTopology)
- [The Release Wave — one emitter, and who resolves the digest](TheReleaseWave)

### Contributing docs

- **Start here:** [Authoring Documentation](AuthoringDocumentation)
- [Docs Follow The Functionality](DocsFollowTheFunctionality) — which repo a page belongs in, and what pins the rest here
- [Specifying Software](SpecifyingSoftware)
- [Glossary](/Doc/Glossary)
- [Developing from within MeshWeaver](DevelopingFromMeshWeaver)
- [Shared Rule Blocks](SharedRuleBlocks)

### Licensing

- **Start here:** [Dependency Licensing](DependencyLicensing) — Apache-2.0/MIT compatible only; the CI gate that enforces it
- [Dependency Major Upgrades](DependencyMajorUpgrades) — the five things a green `-warnaserror` build cannot see at a major boundary (in-mesh source, authored content, the satellites that import this repo's package list, behaviour behind an unchanged signature, and rules an analyzer would have enforced had the build actually loaded it), the differential method that replaces them, and the ledger of boundaries actually crossed

## Getting started

New to the platform? Read [Specifying Software](SpecifyingSoftware) to learn how to write iterative specifications closely aligned with implementation, skim the [Glossary](/Doc/Glossary) for the vocabulary, then explore the full catalog of architecture topics above.
