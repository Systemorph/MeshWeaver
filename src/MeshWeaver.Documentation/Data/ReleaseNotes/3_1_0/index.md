---
Name: MeshWeaver 3.1.0
Category: Release Notes
Description: The first clean release of the 3.x line — a modular platform that installs, updates and verifies itself; a tabbed home with apps and threads; live language services for NodeType authoring; German throughout; and a release that is a promotion of a tested continuous build, never a rebuild.
Icon: Rocket
---

# MeshWeaver 3.1.0

**3.1.0 is the first clean release of the 3.x line.** The line ran as `3.0.0-rc1` through
`3.0.0-rc13` between 2026-08-13 and 2026-08-31 and was never cut clean: the candidates were
rebuilt on tagging rather than promoted, and SemVer sorts `rc13` below `rc2`, so the last four were
invisible to anything that asked for "the newest". Both defects are gone with this release, and the
number moved to `3.1.0` so that every continuous build since sorts above every `3.0.0-*` ever
published (details in [Release Process & Versioning](/Doc/Architecture/ReleaseProcess)).

Everything below shipped as continuous builds and has been running on production portals for
weeks. The day-by-day record — 837 entries since 2026-07-08, 668 of them fixes — is the
**What's New** feed under Settings; this page is the map.

---

## A release is a promotion

`3.1.0` is the same bytes as one `3.1.0-ci.<n>` build. Pushing the annotated tag resolves that
build's already-promoted, already-sealed image set, retags it with the clean version, copies the
release marker under that name, publishes this page as the GitHub Release, and opens the pull
request that moves the line to `3.2.0`. Nothing is compiled twice, so what is released is exactly
what was tested, baked and sealed. NuGet publication is retired with the rc line: modules compile
against the platform image, never against a package feed.

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) · [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy)
- [A release can no longer ship modules that disagree about the platform](/Doc/WhatsNew/2026-09-05-a-release-can-no-longer-ship-modules-that-disagree)
- [A module built once is not built again](/Doc/WhatsNew/2026-09-02-a-module-built-once-is-not-built-again)

## The platform is modular

The AI engine, the view packs, approvals, import, mail, Teams, the MCP server, web search, course
delivery, collaboration and the AKS update mechanics all left the platform image and ship as
modules — installed and updated from the Store, carrying their own dependency closures, gated by
the framework identity they were built against. A new installation arrives with the platform
plugins already installed and granted; a plugin install brings its dependencies with it; a
release states which packages it can carry and where their assemblies are.

- [The built-in agents are a plugin now](/Doc/WhatsNew/2026-08-05-agents-are-a-plugin) · [Plugins can update themselves](/Doc/WhatsNew/2026-08-05-plugins-update-themselves)
- [View packs load as modules](/Doc/WhatsNew/2026-08-15-gui-pack-modules) · [View packs update without waiting for the platform](/Doc/WhatsNew/2026-08-25-view-packs-install-from-the-store)
- [Modules install and update from the Store](/Doc/WhatsNew/2026-08-16-modules-install-and-update-from-the-store) · [Installing a plugin brings its dependencies with it](/Doc/WhatsNew/2026-08-08-install-brings-its-dependencies)
- [New installations arrive with the plugins already installed](/Doc/WhatsNew/2026-08-07-default-package-install) · [New installations get the platform plugins automatically](/Doc/WhatsNew/2026-08-07-default-plugin-grants)
- [A release now says which packages it can carry](/Doc/WhatsNew/2026-08-17-releases-say-which-packages-they-can-carry) · [A release says where its assemblies are](/Doc/WhatsNew/2026-08-17-a-release-says-where-its-assemblies-are)
- Module lanes: [Approvals](/Doc/WhatsNew/2026-08-16-approvals-module), [Excel and CSV import](/Doc/WhatsNew/2026-08-17-import-is-a-module), [Mail over Microsoft Graph](/Doc/WhatsNew/2026-08-17-mail-over-microsoft-graph-is-a-module), [the Teams channel](/Doc/WhatsNew/2026-08-17-the-teams-channel-is-a-module), [the MCP server](/Doc/WhatsNew/2026-08-17-the-mcp-server-is-a-module), [web search](/Doc/WhatsNew/2026-08-17-web-search-is-a-module-you-can-leave-out), [course delivery](/Doc/WhatsNew/2026-08-17-course-delivery-is-a-module), [comments and track changes](/Doc/WhatsNew/2026-08-29-collaboration-is-a-module), [the AKS update mechanics](/Doc/WhatsNew/2026-08-17-aks-update-mechanics-are-a-module), [LinkedIn publishing](/Doc/WhatsNew/2026-08-16-social-module), [the mesh gRPC link](/Doc/WhatsNew/2026-08-16-grpc-link-module)
- [Commercial plugins need a Global Admin, and orphaned install records can be removed](/Doc/WhatsNew/2026-08-08-catalog-governance) · [A plugin's paywall is now part of its type](/Doc/WhatsNew/2026-08-08-type-declared-plugin-gate)

## Installations update, verify and describe themselves

Content is compiled once on CI and adopted at boot, so a platform update prepares in about a
minute and no installation bakes at startup. An update waits for the packages it needs and, when
it is held, says why. Every installation can state exactly what it runs, the header names the
build serving you and when it was deployed, and a fresh install asks to be set up instead of
assuming. Local installations consume the cloud registry, update from it, and arrive through
Homebrew.

- [Deployments start faster — content is compiled once on CI and reused](/Doc/WhatsNew/2026-08-16-ci-bake-seeds-at-boot) · [No startup bake anywhere](/Doc/WhatsNew/2026-08-17-no-startup-bake-compile-on-demand) · [Every platform update now prepares in about a minute](/Doc/WhatsNew/2026-08-11-batch-bake-default-on)
- [Updates now wait for the packages they need](/Doc/WhatsNew/2026-08-17-b-updates-wait-for-the-packages-they-need) · [A blocked update now tells you why](/Doc/WhatsNew/2026-08-10-a-blocked-update-now-tells-you-why) · [Updates recompile content only when the API actually changed](/Doc/WhatsNew/2026-08-16-rebuild-only-on-breaking-change)
- [An installation can now state exactly what it is running](/Doc/WhatsNew/2026-08-10-an-instance-can-state-what-it-runs) · [The header names the build serving you](/Doc/WhatsNew/2026-08-17-the-header-names-the-build-serving-you) · [The header says when this build was deployed](/Doc/WhatsNew/2026-08-18-last-deployed-in-the-header)
- [A fresh install now asks you to set it up](/Doc/WhatsNew/2026-09-03-a-fresh-install-asks-instead-of-assuming) · [An installation asks before it registers](/Doc/WhatsNew/2026-08-30-an-installation-asks-before-it-registers) · [Instances get an identity, a licence and a setup app](/Doc/WhatsNew/2026-08-30-instances-get-an-identity-a-licence-and-a-setup-app)
- [A local install consumes the cloud registry — and Homebrew delivers it](/Doc/WhatsNew/2026-08-30-a-local-install-consumes-the-cloud-registry) · [Local installs auto-update from the registry](/Doc/WhatsNew/2026-08-16-local-autoroll-acr) · [A local portal serves every plugin repo you have checked out](/Doc/WhatsNew/2026-08-31-local-portal-serves-every-checked-out-repo)
- [The licence lives on the instance, and every package fetch checks it](/Doc/WhatsNew/2026-08-30-the-licence-lives-on-the-instance) · [A registry grant can name a plan](/Doc/WhatsNew/2026-08-30-a-registry-grant-can-name-a-plan) · [The mesh trusts a build without holding a secret](/Doc/WhatsNew/2026-09-01-the-mesh-trusts-a-build-without-holding-a-secret)
- [A platform release reaches plugin repositories through memex](/Doc/WhatsNew/2026-09-03-a-platform-release-reaches-plugin-repositories-through-memex) · [The cluster can no longer hide configuration](/Doc/WhatsNew/2026-08-30-the-cluster-can-no-longer-hide-configuration)

## Home, apps and navigation

The home is tabbed — apps on top, one content list, pins and spaces — with one search across it.
Threads have their own app, apps can be arranged in groups, the Store opens with its categories,
and your deployments appear in the mesh switcher. A click can open a thread beside the page, a
suggested prompt is something you can run, and a page inside a package wears that package's mark.

- [The tabbed home — apps, pins, and spaces](/Doc/WhatsNew/2026-08-21-apps-home-tabs) · [A home with apps on top and one content list](/Doc/WhatsNew/2026-08-24-home-apps-and-content) · [One search across your home tabs](/Doc/WhatsNew/2026-08-22-home-shared-search) · [Arrange your apps in groups](/Doc/WhatsNew/2026-09-03-arrange-your-apps-in-groups)
- [The Threads app](/Doc/WhatsNew/2026-08-21-threads-app) · [A click can open a thread beside the page](/Doc/WhatsNew/2026-09-02-a-click-can-open-a-thread-beside-the-page) · [A suggested prompt is now something you can run](/Doc/WhatsNew/2026-09-02-a-suggested-prompt-is-now-something-you-can-run) · [Pages can point at the chat](/Doc/WhatsNew/2026-08-24-chat-hint-pulse)
- [The Store opens with its categories](/Doc/WhatsNew/2026-09-03-the-store-opens-with-categories) · [Your deployments appear in the mesh switcher](/Doc/WhatsNew/2026-08-21-deployments-in-switcher) · [Official vendor logos on store cards](/Doc/WhatsNew/2026-08-28-official-vendor-marks)
- [The node menu groups related actions into sub-menus](/Doc/WhatsNew/2026-08-12-the-node-menu-groups-related-actions-into-sub-menus) · [Recycle is one click in the node menu](/Doc/WhatsNew/2026-08-25-recycle-action) · [Delete by link — a single node or a whole query result](/Doc/WhatsNew/2026-08-22-delete-a-set-by-url) · [Moved pages keep their links working](/Doc/WhatsNew/2026-08-12-moved-node-redirects)
- [Presentation mode hides what you would rather not share](/Doc/WhatsNew/2026-08-21-presentation-mode) · [Every page shows its own icon](/Doc/WhatsNew/2026-08-11-pages-show-their-own-icon) · [A page inside a package now wears that package's mark](/Doc/WhatsNew/2026-09-02-a-page-wears-its-packages-mark) · [Breadcrumb trail in the portal](/Doc/WhatsNew/2026-07-10-portal-breadcrumbs)
- [Menu wording and grouping can be changed without a release](/Doc/WhatsNew/2026-08-12-menu-wording-and-grouping-can-be-changed-without-a-release) · [Actions that run when you sign in](/Doc/WhatsNew/2026-08-24-logon-actions)

## Documents, decks and collaboration

Select any text and comment on it; track changes come from the document's own history and the
redline lives in the version comparison. Link previews render as cards, decks export exactly as
they look, slide shows carry the whole deck, and a document can be shared as an email in your own
name. GitHub sync is two-way and compares what you authored, not compile bookkeeping.

- [Select text and comment — on any content](/Doc/WhatsNew/2026-08-01-comment-on-any-selection) · [Track changes now come from the document's history](/Doc/WhatsNew/2026-08-08-tracked-changes-from-version-history) · [The redline moved to where you ask for it](/Doc/WhatsNew/2026-08-09-redline-lives-in-the-version-comparison)
- [Link-preview cards in documents](/Doc/WhatsNew/2026-08-11-link-preview-cards-in-documents) · [Decks can now export exactly as they look](/Doc/WhatsNew/2026-08-09-decks-can-export-exactly-as-they-look) · [Slide shows can carry the whole deck at once](/Doc/WhatsNew/2026-08-26-slide-shows-can-carry-the-whole-deck) · [Instant slide navigation](/Doc/WhatsNew/2026-07-23-instant-slide-navigation)
- [Share a document as an email, in your own name](/Doc/WhatsNew/2026-08-11-email-a-document-instead-of-attaching-it) · [Standard analysis views ship as controls](/Doc/WhatsNew/2026-08-08-standard-analysis-views) · [Maps become provider-neutral](/Doc/WhatsNew/2026-08-15-map-control) with [OpenStreetMap and Apple MapKit providers](/Doc/WhatsNew/2026-08-15-map-providers)
- [Two-way GitHub sync — your edits are kept](/Doc/WhatsNew/2026-07-14-gitsync-two-way) · [GitHub sync now compares what you authored](/Doc/WhatsNew/2026-08-02-nodetype-sync-owns-authored-content) · [GitSync updates recompile what they change](/Doc/WhatsNew/2026-08-06-gitsync-updates-recompile)
- [Course videos now ship with the course](/Doc/WhatsNew/2026-08-08-courses-ship-their-videos) · [The course index now shows the whole course and marks where you are](/Doc/WhatsNew/2026-08-08-whole-course-index)

## Language services for NodeType authoring

Roslyn-backed language services run over every NodeType's live compilation and drive three
surfaces from one backend: the Coder agent's `Lsp` plugin (`LspCheckNode`, `LspDiagnosticsForNode`,
`LspHoverForNode`, `LspCompletionsForNode`), the same four tools on the MCP server for Claude Code
and any other MCP client, and live squiggles in the portal's Monaco editor. `LspCheckNode` is a
full-substitution pre-flight: it rebuilds the whole source set with the proposed file in place, so
cross-file breakage is caught before a commit, and `#r "nuget:…"` directives resolve through the
same resolver the production compile uses.

Around it: a page waiting on a compile shows live progress, a compile reports what it costs per
type, rebuilds scope to what a type actually uses, a sync or install no longer reverts a type's
compile state, and a node type can say where its instances live.

- [A page waiting on a compile now shows live progress](/Doc/WhatsNew/2026-08-11-a-page-waiting-on-a-compile-now-shows-live-progress) · [What a NodeType compile costs, per type](/Doc/WhatsNew/2026-08-13-what-a-nodetype-compile-costs) · [Rebuilds scope to what a type actually uses](/Doc/WhatsNew/2026-08-17-rebuilds-scope-to-what-a-type-actually-uses)
- [A sync or install no longer reverts a type's compile state](/Doc/WhatsNew/2026-08-08-nodetype-compile-state-survives-sync) · [A node type can now say where its instances live](/Doc/WhatsNew/2026-09-02-node-types-declare-where-instances-live) · [Why a green build does not mean your node types still compile](/Doc/WhatsNew/2026-08-09-green-build-does-not-mean-node-types-compile)
- [Builds are coordinated by nodes, not by lease files](/Doc/WhatsNew/2026-08-13-builds-are-coordinated-by-nodes-not-by-lease-files) · [Builds can run in their own disposable process](/Doc/WhatsNew/2026-08-13-builds-can-run-in-their-own-disposable-process) · [The compiler is its own assembly](/Doc/WhatsNew/2026-08-17-the-compiler-is-its-own-assembly)

## AI

Models are picked by what they are for and Auto is the default; agents keep working files in the
mesh and load their guidance on demand; Claude Code and GitHub Copilot are opt-in per user;
OpenRouter threads cache their prompts and the token counter counts cached tokens.

- [Models are picked by what they are for — and Auto is the new default](/Doc/WhatsNew/2026-08-09-model-tiers-and-auto) · [Auto picks the right model for the job](/Doc/WhatsNew/2026-08-21-auto-picks-the-right-model)
- [Agents can keep working files, and they live in the mesh](/Doc/WhatsNew/2026-08-06-agent-working-files) · [Agent guidance loads on demand](/Doc/WhatsNew/2026-08-28-agent-guidance-loads-on-demand)
- [Claude Code and GitHub Copilot are now opt-in per user](/Doc/WhatsNew/2026-08-02-cli-harnesses-opt-in) · [OpenRouter threads now cache their prompts](/Doc/WhatsNew/2026-08-18-openrouter-prompt-caching) · [Token counter now counts cached tokens](/Doc/WhatsNew/2026-07-08-token-usage-cache-counter)
- [Send feedback right from chat with /feedback](/Doc/WhatsNew/2026-07-11-feedback-skill) · [New /thread skill](/Doc/WhatsNew/2026-07-30-thread-skill) · [AI models now show their provider's logo](/Doc/WhatsNew/2026-07-10-model-provider-icons)

## The portal speaks German

Every platform string ships in English and German, on the Blazor portal and on the web and
mobile clients alike, and you choose your language when you sign up. Authored content renders as
authored; platform chrome inside it follows the viewer.

- [The portal speaks German](/Doc/WhatsNew/2026-08-04-portal-speaks-german) · [The web and mobile clients speak German, and render everything the portal renders](/Doc/WhatsNew/2026-08-06-js-clients-catch-up-with-blazor) · [Choose your language when you sign up](/Doc/WhatsNew/2026-08-17-choose-your-language)

## Clients

The React frontend looks and works like the portal, the new home is on React, React Native and
Next.js, the portal can run as Blazor, the Next shell, or both, mobile deployments are
configurable, and your browser tabs tell themselves apart.

- [The React frontend looks and works like the portal](/Doc/WhatsNew/2026-07-12-react-frontend-styling-overhaul) · [The new home on the React, React Native and Next.js clients](/Doc/WhatsNew/2026-08-24-js-clients-home-design)
- [Choose your portal: Blazor, the new Next shell, or both](/Doc/WhatsNew/2026-08-24-optional-blazor-gui-shells) · [Configurable mobile-app deployments](/Doc/WhatsNew/2026-08-24-rn-configurable-deployments) · [The mobile app keeps its mesh list in your local mesh](/Doc/WhatsNew/2026-08-20-mobile-mesh-list-in-local-mesh)
- [Your browser tabs tell themselves apart](/Doc/WhatsNew/2026-08-18-your-browser-tabs-tell-themselves-apart) · [UI extensibility guide, for every renderer](/Doc/WhatsNew/2026-08-14-ui-extensibility-docs)

## Data and storage

A Snowflake storage backend joins Postgres; many nodes are created in one round-trip and plugin
installs land their nodes in bulk; durable streams are mesh nodes; long activity logs stay fast,
old notifications clear themselves away, and interactive kernels release their memory.

- [Snowflake storage backend](/Doc/WhatsNew/2026-07-13-snowflake-storage-backend) · [One round-trip to create many nodes](/Doc/WhatsNew/2026-08-05-bulk-create-mesh-verb) · [Plugin installs are minutes faster — nodes now land in bulk](/Doc/WhatsNew/2026-08-05-bulk-save-node-repo-installs)
- [Durable streams are mesh nodes](/Doc/WhatsNew/2026-08-30-durable-streams-are-mesh-nodes) · [Long activity logs stay fast](/Doc/WhatsNew/2026-08-13-activity-logs-stay-fast) · [Old notifications now clear themselves away](/Doc/WhatsNew/2026-09-04-old-notifications-now-clear-themselves-away) · [Interactive kernels now release their memory](/Doc/WhatsNew/2026-07-22-kernel-memory-unload)

## Operating a deployment

Errors in production open their own tickets; imports and startup builds report what they cost;
each environment declares what it carries; a deployment can be checked for currency from inside
or outside; admins enroll a user in one step; and there is a playbook for host crashes.

- [Errors in production open their own tickets](/Doc/WhatsNew/2026-08-07-red-log-ticketing) · [Imports and startup builds report what they cost](/Doc/WhatsNew/2026-08-12-imports-and-startup-builds-report-what-they-cost) · [Frame-loss lines in the log are a recovery counter](/Doc/WhatsNew/2026-08-30-frame-loss-in-the-log-is-a-resync-counter)
- [Each environment declares what it carries](/Doc/WhatsNew/2026-08-17-each-environment-declares-what-it-carries) · [Checking that a deployment is up to date, from inside or outside](/Doc/WhatsNew/2026-08-09-check-a-deployment-is-current) · [Instance keys rotate through the Hosting operator](/Doc/WhatsNew/2026-08-30-instance-keys-rotate-through-the-hosting-operator)
- [Admins can enroll a user in one step](/Doc/WhatsNew/2026-08-08-admin-enroll-action) · [Coupons administration tab](/Doc/WhatsNew/2026-08-02-coupons-administration-tab) · [Invite a whole list of emails to a group](/Doc/WhatsNew/2026-07-11-group-bulk-invite) · [See at a glance what is public](/Doc/WhatsNew/2026-08-10-see-what-is-public)
- [A playbook for host crashes](/Doc/WhatsNew/2026-08-28-crash-triage-playbook) · [A guide for standing up a new repository](/Doc/WhatsNew/2026-08-28-new-repo-skill) · [What's New now covers every repository](/Doc/WhatsNew/2026-08-28-whats-new-from-every-repo)

## How the platform itself is built

Core `main` merges through a merge queue with a steward; a platform change runs the plugin suites
before it merges; a change that spans two repositories lands in the right order; a rule shared
between repositories can no longer drift apart unnoticed; and the test tree holds zero hand-woven
concurrency gates.

- [The merge queue runs itself](/Doc/WhatsNew/2026-09-02-the-merge-queue-runs-itself) · [A platform change now runs the plugin suites before it merges](/Doc/WhatsNew/2026-09-03-a-platform-change-now-runs-the-plugin-suites-before-it-merges) · [A change that spans two repositories now lands in the right order](/Doc/WhatsNew/2026-09-02-a-change-that-spans-two-repositories-lands-in-the-right-order)
- [A rule shared between repos can no longer drift apart unnoticed](/Doc/WhatsNew/2026-08-30-agents-md-rules-can-no-longer-drift-between-repos) · [The test tree holds zero hand-woven concurrency gates](/Doc/WhatsNew/2026-08-30-the-test-tree-holds-zero-hand-woven-gates) · [Hand-woven concurrency gates cannot come back](/Doc/WhatsNew/2026-08-30-hand-woven-concurrency-gates-cannot-come-back)

---

## Upgrading from a 3.0.0-rc build

- **Nothing to do for a running installation.** Every `3.1.0-ci.<n>` build sorts above every
  `3.0.0-*`, so a Continuous install rolls forward on its own; a Stable install takes `3.1.0`.
  Module bundles are keyed by framework identity and re-baked per set, so the assembly version
  moving to `3.1.0.0` changes nothing an install adopts.
- **Module floors are satisfied.** A module declaring `minMeshVersion: 3.0.0-rc8` (or any
  `3.0.0-*`) runs on `3.1.0`; the floor is judged as a regression check, never absolutely
  ([Release gates](/Doc/Architecture/ReleaseGates)).
- **NuGet is retired.** The last packages on nuget.org are `3.0.0-rc13` for the framework and
  `3.0.0-rc7` for `MeshWeaver.Hosting.PostgreSql`, `MeshWeaver.AI` and `MeshWeaver.Blazor`, which
  moved to MeshWeaver.Plugins and ship as bundles. Nothing in the fleet restores them; they stay
  listed as history. Build a module against the platform image
  ([Plugin packaging](/Doc/Architecture/PluginPackaging)).
- **Deck and slide types live in the Publish package**, the AI engine in MeshWeaver.Plugins, the
  view packs, maps and collaboration in their modules — an installation that had them installed
  keeps them; one that did not installs them from the Store.

## Fixes

668 fixes shipped on this line, bundled by day in the **What's New** feed. The classes that
recur are worth naming because each now has a guard: hand-woven concurrency gates and async
bridges on the actor model (the test tree holds zero and cannot regain one), writes that ran as
the hub instead of the user (every write primitive carries the caller's access context), point
reads of nodes that may not exist yet (the storm-breaker no longer suppresses the write it is
waiting for), and builds that produced two copies of one module for one identity (a release can
no longer ship modules that disagree).
