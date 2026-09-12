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
- [Aggregating Providers](AggregatingProviders)
- [Hub Disposal Model](HubDisposalModel)
- [Transient Node Probes](TransientNodeProbes) — a probe hub's own address is not a node
- [Executive Assistant Credential Reads](ExecutiveAssistantCredentialReads) — a turn that waits for a mesh read holds the queue that read's reply must travel through, and the timeout was rendered as "you never connected"
- [Bounds Must Be Ordered](BoundsMustBeOrdered)
- [Message-Based Communication](MessageBasedCommunication)
- [No Static State](NoStaticState)
- [Observable Hub Pipeline (migration design)](ObservableHubPipeline)
- [Per-Hub TaskScheduler — Actor Isolation Across the Mesh](OrleansTaskScheduler)
- [Removing Hand-Woven Concurrency Gates](RemovingHandWovenGates)
- [Removing Observable-to-Task Bridges](RemovingObservableToTaskBridges)
- [JSON Serialization](Serialization)

### Reading & writing nodes

- **Start here:** [CQRS — Queries vs. Content Access](CqrsAndContentAccess)
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
- [The Phantom Base After Owner Disposal](PhantomBaseAfterOwnerDisposal) — the owner echoes a merge BEFORE it flushes it, so a "never applied" NACK's re-attempt can find its own unpersisted write in the mirror, diff to nothing and report success
- [Conditional Writes Across Hubs](ConditionalWritesAcrossHubs) — on a node you do not own the lambda runs on YOUR mirror and ships a diff, so a field it decided not to write is absent from the patch and a concurrent write to it survives
- [Live Mirrors and the Change Feed](LiveMirrorsAndTheChangeFeed) — a write must not end its own streams
- [Stream Liveness and the Hub Reference](StreamLivenessAndTheHubReference) — a stream outlived the hub it held, and the contract said it could not
- [The sync/ Hub Population](SyncHubPopulation) — a `sync/` hub is one field of a stream and a subscription makes two of them; the only reaper of a Started one is an idle sweep the traffic keeps re-arming
- [The Evicted-Stream Retention](EvictedStreamRetention) — a change-feed eviction parks a remote stream and `ReclaimIfUnheld` refuses to dispose one that carries no lease entry, so every unleased call site retains one stream, and two `sync/` hubs, per change event
- [The Read Path Minted a Hub Per Read](ReadPathStreamMinting) — a live in-process census decomposed a replica's `sync/` hubs into their holders and pinned the growth on the read path: a constant configuration took `GetDataRequest` out of the stream cache, so every read left a permanent hub behind (six reads, six hubs, measured on the running portal)
- [The Recursive-Delete Drain](RecursiveDeleteDrain) — the plan is a snapshot the removals may exceed, the completion check must include the ROOT, and the stage bound measures progress, not duration
- [Business Rules & Calculations](BusinessRules)
- [Data Versioning Strategies](DataVersioning)
- [Mesh Graph Architecture](MeshGraph)
- [MeshNode Versioning](MeshNodeVersioning)
- [Query Provider Parity](QueryProviderParity)
- [Query Result Scoring](QueryResultScoring)
- [Reading a Write Verdict](ReadingAWriteVerdict)
- [Satellite Entity Patterns](SatelliteEntityPatterns)
- [Satellite Node Patterns](SatelliteNodePatterns)
- [Repairing a Stale MainNode — When the Broken Field Guards Itself](StaleMainNodeRepair)
- [Synced Mesh Node Queries](SyncedMeshNodeQueries)
- [Update Validators See Typed Content](UpdateValidatorsSeeTypedContent)

### Storage & partitions

- **Start here:** [Postgres Schema Architecture](PostgresSchemaArchitecture)
- [Partition Storage Routing](PartitionStorageRouting)
- [Partition Teardown](PartitionTeardown) — deleting a partition ROOT drops its backing store; keyed on the node's SHAPE, never its NodeType
- [Partition Storage Hubs](PartitionStorageHubs)
- [Partitioned Persistence](PartitionedPersistence)
- [Storage Adapter Implementation](StorageAdapterImplementation)
- [Change-Feed Isolation](ChangeFeedIsolation) — one throwing subscriber must never starve the others
- [A Container Registry in Memex](ContainerRegistryInMemex) — PROPOSAL: serving OCI images from the mesh, and the bootstrap circularity that keeps the boot image on ACR
- [Static Repo Import](StaticRepoImport)
- [The Prune Requires a Complete Listing](PruneRequiresACompleteListing) — an import prunes on "absent from the source ⇒ deleted"; a truncated GitHub tree arrives as HTTP 200 and turns every unread file into a deletion
- [Import Write Ordering](ImportWriteOrdering) — a NodeType lands before the instances that name it
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
- [OWASP ZAP Scan — 3.0.0 (6 September 2026)](SecurityScan_3_0_0)
- [OWASP ZAP Scan — Every Release](SecurityScanning)

### Threads, activities & AI

- **Start here:** [Thread Operations](ThreadOperations)
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
- [Compile Cache Input Freshness](CompileCacheInputFreshness) — verify the captured input before reusing a DLL that finished after a source edit
- [Execute-Time Interlock](ExecuteTimeInterlock) — a build proven stale is never armed
- [Emit Reference Capture](EmitReferenceCapture) — bounded, opt-in CI evidence for runtime compiler failures
- [Graph / Compiler Layering](GraphCompilerLayering) — the four assemblies, the cycle, and the full-MVID size rule
- [Toolchain Re-evaluation Lane](ToolchainReevaluationLane) — why a toolchain change stopped rebaking the world
- [The Dependency Record Floor](DependencyRecordFloor) — a record's module entry says "I need at least X", not "I need exactly this build"; the MVID pin that could not converge because Roslyn hashes absolute source paths, the two-replica recompile ping-pong it produced, and the four things the floor deliberately does not relax
- [Producer Determinism of the Dependency Record](ProducerDeterminismOfTheDependencyRecord) — the same content must stamp the same record however the producer reached its bytes; the disk-cache hit that shipped a weaker guard, and why the digest is persisted beside the bytes rather than recomputed
- [Rebake Waves](RebakeWaves) — why a roll rebakes the world anyway, and what one rebake writes
- [Source-Set Establishment](SourceSetEstablishment) — a resolved source set of ZERO is ambiguous, and only the type's own persisted snapshot tells "owns no Code" from "the discovery pass came back short"; the boot that resolved 91 fewer Code nodes than its neighbours and held a portal out of rotation for the startup probe's full three hours
- [Missing Declared Sources](MissingDeclaredSources) — emptiness measured on the UNION is invisible for any type that also draws on a shared library; the NodeType whose own Source subtree was gone, reported itself as broken C#, and had the identical doomed compile taken again on every boot for four days
- [Install-Time Prebuilt Adoption](InstallTimePrebuiltAdoption) — the only lane that serves a package installed AFTER boot, and the four answers its zero must keep apart because a silent non-adoption reads exactly like a successful one
- [Adoption and the Sweep Count Different Things](AdoptionAndTheSweepCountDifferentThings) — the cold boot that adopted 78 prebuilt assemblies and then reported 5, with nothing wrong on the share: what each instrument counts, why the sweep could not see its own process's writes, and the node-version ordering that keeps the fix from becoming a stale serve
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
- [The Sync-Ref Contract](SyncRefContract) — an UNATTENDED import reads a commit CI proved; only a person clicking Update may read a branch tip, and resolving the ref twice put sources no build had compiled onto two production portals for five hours
- [Node Type Compilation](NodeTypeCompilation)
- [The Platform Image's Closure](PlatformImageClosure) — the image IS the reference set every satellite's modules compile against; the two invariants, and why every consumer used to discover them by failing to compile
- [Sealed Publication Reads](SealedPublicationReads) — a publication is unreadable for ~90s per target per publish and the `plugins` prefix has two writers; the three answers (404/503/412), the generation that pins one instance, and the mix no reader can detect
- [Sealed Publication Generations](SealedPublicationGenerations) — the layout that makes that mix UNREPRESENTABLE (a directory per publication plus a pointer swapped last), the reader contract, the retention rule, and the ordered migration that avoids a new-writer/old-writer half-state
- [Install Completeness](InstallCompleteness) — what an install RECORD declares landed, compared against what is actually in the mesh; the comparison nothing made until #3485, and why only one of its five verdicts is a pass
- [CI Content Bake](CiContentBake)
- [Bundle Delivery Stages](BundleDeliveryStages) — the four independent stages between a merge and a portal serving prebuilt bytes (write · compose · select · deliver), which of #3461 / #3732 / #3768 / #3583 owns each, the instrument that answers for each — and why a reading taken at one stage is not evidence about another
- [Prebuilt Bundle Retention](PrebuiltBundleRetention) — the sweep that prunes what CI bakes: where it is registered (and why "zero callers" was measured twice and wrong both times), the deletion default that is `true` in code and `false` in the chart, the report that names its denominator, and the pinned satellite gate the protected set cannot see
- [Deploying a plugin change — merging is not shipping](DeployingPluginChanges)
- [Module Adoption Policy](ModuleAdoptionPolicy)
- [Module Build Architecture](ModuleBuildArchitecture)
- [Module Closure Accounting](ModuleClosureAccounting)
- [The Module Identity Anchor](ModuleIdentityAnchor)
- [Module-Owned Siblings Ride](ModuleOwnedSiblingsRide)
- [The Module Platform Link Gate](ModulePlatformLinkGate)
- [Rolling-Update Build Tolerance](RollingUpdateBuildTolerance) — why a rolling platform update recompiled Store/Plugin ten times in four minutes and blanked every instance behind "build did not settle within 30s" (two generations, one record, one framework identity per record), and the rules that let an instance render on the last build its process can load instead
- [The Module Publication Gate](ModulePublicationGate) — a bundle used to reach the live registry from inside its own pack leg, before the sibling suites, the portal-host shards, the compile-check and the Tests-area gate had reported; the hand-over moved downstream of the full source verdict, and what it refuses (failed, skipped, cancelled, missing, foreign-lane, substituted)
- [Module Generation Substitution](ModuleGenerationSubstitution) — `Assembly.LoadFrom` does not promise to load the path it is handed: a byte-identical copy the load context already holds is returned instead, silently, so the loader recorded the generation it ASKED for while the process ran another; the three answers that replace two
- [Module Set Convergence](ModuleSetConvergence)
- [Module Versioning](ModuleVersioning)
- [Modules](Modules)
- [Package Mark Inheritance](PackageMarkInheritance)
- [Pin-Boundary Contracts](PinBoundaryContracts)
- [Platform and content — two layers, two cadences](PlatformAndContent)
- [Platform Build Identity](PlatformBuildIdentity)
- [The Platform-Shipped Witness](PlatformShippedWitness)
- [The Plugin Build Contract](PluginBuildContract)
- [Plugin Bundles in the Registry](PluginBundlesInTheRegistry)
- [Plugin Publication Provenance](PluginPublicationProvenance) — the signed publication callback names the CONTENT commit that was built and the platform version read from the selected portal image, never the calling workflow's commit or event; core CD building Plugins used to announce a core sha as a Plugins commit
- [Plugin Update on Green Build](PluginUpdateOnGreenBuild)

### Reliability & wedges

- **Start here:** [Error Propagation & Wedges](ErrorPropagationAndWedges) — drive wedges to 0
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
- [Ambient Test-Host Hangs](AmbientTestHostHangs) — what decides whether a killed test host can be diagnosed at all, and the readings of it already falsified
- [In-Mesh Tests and the Seal](InMeshTestsAndTheSeal) — a Tests area no required context executes is a latent trunk red the seal detonates fleet-wide; how to measure a gate before requiring it
- [Cancel and Join Are Two Questions](CancelAndJoinSequencing) — a deadline that asks work to stop and a deadline that waits for it to have stopped must not share one clock
- [Collection-Scoped Test Fixtures](CollectionScopedTestFixtures)
- [Debugging Native Crashes (core dumps)](DebuggingNativeCrashes)
- [Debugging Postgres in Prod / Test](DebuggingPostgres)
- [Decentralised Tests](DecentralisedTests)
- [Gate Content Assets](GateContentAssets)
- [In-Mesh Build and Test](InMeshBuildAndTest)
- [Orleans Test Routing Pattern](OrleansTestRoutingPattern)
- [Reading CI Signals](ReadingCiSignals)
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
- [The Continuous Delivery Contract](ContinuousDeliveryContract) — all-or-nothing publication; verify the image, never the tick
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
- [The Image Tag Contract](ImageTagContract) — which image tags the promotion actually publishes, why the portal has no `latest`, and the two-writer history of the one that had no producer at all: retired lane, then retention, and every check green throughout
- [Pin Set Consistency](PinSetConsistency) — every pinned digest EXISTING is not every pinned digest naming the same BUILD; the invariants that red a half-moved set, three written deliberately weaker than the obvious version, and the falsification that found the vacuity trap inside the gate itself
- [Duplicate Keys in Workflow YAML](WorkflowDuplicateKeys) — a duplicate mapping key is accepted silently and the LAST one wins, so a pin can move in the diff and not in the job; the near-miss, why every existing gate was blind, and the guard that names the file, the key and both lines at the first job
- [Image Pair Skew](ImagePairSkew) — a promoted image pairs a core commit with a Plugins head resolved hours later; each half green, the pair never run (the 2026-09-03 sign-in outage)
- [The Merge Queue](MergeQueue) — one entry built at a time so nothing churns, and a steward that re-queues an ejected PR on evidence and never re-runs
- [Carving Projects Out Of Core](CarvingProjectsOutOfCore) — what a SOURCE move costs and what it does not
- [Red-Log Watching & Ticketing](LogWatchTriage) — every `fail:`/`crit:` becomes exactly one triaged issue
- [Log Entries Are a Query Result, Not a Feed](LogEntriesAreAQueryResult) — `Hosting/LogEntry` is the output of one `Logs` action, so an absence in it is evidence of nothing; the denominator printed on every row, the level that lives on a different node, and how to ask for a line that carries an answer
- [Verifying Chart Values](VerifyingChartValues) — a key can be set, reach the render, and still not be read; why the obvious gate was vacuous for the one component it existed to guard, and the binary check that closes it (the drain that erased every namespace's log history)
- [Measuring a Live Portal Read-Only](MeasuringALivePortalReadOnly) — `/health` first (public, past RLS, a different replica each call), the incident store, the four break-glass instruments, and why an absence needs a coverage fact before it counts as evidence
- [Chart Ownership and the Runner Pool](ChartOwnershipAndRunnerPool) — why the chart's gate is here, what a relocation must carry, and why path-filtering it is unsafe
- [Sharding the Node-Repo Gate](NodeRepoGateSharding) — a cap cut reports as `cancelled`, so the fan-out that removes it, and the fold that keeps ONE required context and ONE gate log
- [Applying Is Not Rolling Out](ApplyingIsNotRollingOut) — helm applies, the caller observes; the fixed fifteen-minute `--atomic --wait` that reverted a correct upgrade mid-startup-gate, and why a bigger timeout only moves the cliff
- [Probe Semantics](ProbeSemantics) — readiness, liveness and startup ask three different questions with three different remedies; why they get three paths and three tags
- [What a Synthetic Probe May Assert](SyntheticProbeTargets) — a probe naming one deployment's installed content is broken by construction; the platform floor, the negative control that tells "absent" from "down", and reading the target's own declaration
- [Why a GC-Bound Pod Stays in Rotation](WhyAGcBoundPodStaysInRotation) — the GC's hard limit sits below the container limit, so a portal short of memory is defended rather than restarted
- [Self-hosted CI runners on AKS](SelfHostedRunners) — ARC beside the portals on one pool; three brakes, a negative priority class, and the reserve arithmetic that decides the cap
- [Candidate Release Protocol](CandidateReleaseProtocol)
- [Chart Drift — what a deploy actually does](ChartDriftSemantics)
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
- [Operating from the portal, not the cluster](OperatingFromThePortal)
- [The Payment Provider Contract](PaymentProviderContract)
- [Pre-Boot Service Substitution](PreBootServiceSubstitution)
- [Project Templates](ProjectTemplates)
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
