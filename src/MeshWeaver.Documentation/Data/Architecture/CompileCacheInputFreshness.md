---
Name: Compile Cache Input Freshness
Category: Architecture
Description: A cached NodeType assembly must describe the captured source and configuration inputs, even when an edit landed before an earlier compilation finished writing its DLL.
Icon: Code
---

# Compile Cache Input Freshness

A DLL's completion time does not establish which source snapshot it compiled. The runtime
compiler therefore compares a cached artifact's **persisted producing input digest** with the
generated input for the candidate snapshot before it reuses the artifact. Identical inputs still
reuse their existing DLL and producer digest. Missing, different or inconclusive input identity
follows the normal compilation path.

This is the same `MeshNodeCompilationService` path used by automatic compilation and the
NodeType's normal Compile action. It does not add a second compiler, disable caching, modify
source timestamps, or alter registry adoption and release hand-over policy.

## The failure

The previous disk-cache predicate required a complete artifact set and a DLL write time at least
as recent as the maximum source/NodeType LastModified and the framework timestamp. That allows
this ordinary ordering:

1. A compilation captures source snapshot A.
2. An edit produces snapshot B while that compilation is running.
3. The A DLL finishes writing after B's LastModified.
4. The next compilation accepts the A DLL for B because its write time is newer.

The false hit also returned the **current B source stamps** beside A's bytes and producing digest.
`CompiledSources` could consequently claim the new source had compiled while the emitted methods
still implemented A. Automatic dirty detection then had a misleading success record to compare.

The normal forced Compile request bypasses dirty/prebuilt short-circuits but still enters this
disk-cache path. Forcing a request alone did not repair its freshness predicate.

## One captured snapshot, one input comparison

`GetAssemblyLocationWithLog` retains its single captured Source/Test snapshot and timestamp-based
candidate lookup. For a candidate with a persisted digest it calls the existing generated-input
regeneration logic with that exact snapshot. The shared path applies the canonical source/test
shaping, include resolution, configuration skeleton, NuGet directive handling and input digest.
It does not independently rediscover source nodes for the comparison.

Only a matching digest permits the hit. The successful hit still uses the **persisted digest**
when stamping the dependency record; the regenerated candidate digest is evidence for the
comparison, never a replacement producer claim. See
[Producer Determinism of the Dependency Record](../ProducerDeterminismOfTheDependencyRecord).

An unresolved NodeType definition is also inconclusive. It cannot be substituted with an empty
configuration to establish cache equality. A present definition whose `Configuration` is null
remains a valid input, so unchanged types with no configuration still take warm cache hits. This
cache guard leaves the inherited fresh-compilation behavior for an unresolved definition unchanged.

The include reader uses the existing `RunAsSystem` boundary for each cold read, including the
fallback read started by an earlier response. It restores the subscribing flow and downstream
callbacks to their caller identity. A long-lived `Observable.Using` impersonation scope would
instead be disposed by the response flow and can leave the subscriber elevated.

The existing re-evaluation entry point keeps its signature, discovery path and inconclusive
result semantics. Neither `CompilationCacheService`'s load-context lifecycle nor the registry's
late hand-over machinery changes here.

## Executed regression

The original regression ran against unmodified core
`6231c4da4c1baa5dbcc9e44b43020006583fcb2c` on 2026-09-11. It uses the real
`IMeshNodeCompilationService.CompileAndGetConfigurations` and its existing `sourcesOverride`
fixture seam, then invokes methods from the emitted assembly under the real scan pin.

The test derives B's LastModified as one tick before the first actual DLL's recorded completion
time. This fixes the interleaving without a sleep, mocked compiler, or file timestamp edit. The
before result was:

```text
Expected: 43|new-case;stampsCurrent=True
Actual:   42|original-case;stampsCurrent=True
Second compiler transcript: Cache hit
```

The source method and test-registration method both remained old while the returned source stamps
claimed B. One case executed and failed; fixture teardown completed cleanly. That historical red
predates the expanded case matrix and is not a claim that every later case was run red.

The final `CompileCacheInputFreshnessTest` checks Source-only, Test-only, combined Source/Test and
NodeType configuration-only changes. Every case owns a distinct type path because the standard
fixture shares disk cache across instances of a test class. The configuration case invokes the
actual emitted `HubConfiguration`, and the source cases inspect both emitted methods and
`CompiledSources`.

The focused selection passed **50/50**, including those four cases and the existing
`CacheHitStampsTheSameRecordTest`, `ContentKeyReevaluationTest` and `GeneratedInputIdentityTest`
controls. The compiler test project built in Release with warnings as errors, with zero warnings
and errors. The unchanged-input control requires a real first emit, a subsequent actual cache
hit, the same artifact path and an identical producer dependency record.

The full compiler suite first ran with the machine's default culture: **842/845 passed**. The
three failures were unchanged `ShortWriteIsNotAPublicationTest` assertions that require comma
grouping (`65,536`, `4,096`, `16,384`) while the local runtime formatted Swiss apostrophes. The test
and the relevant numeric-message formatter files are byte-identical to the baseline. With native
xUnit's explicit `-culture en-US`, those three controls passed **3/3**, then the full compiler
suite passed **845/845**, with no errors, skips or unrun tests. No assertion or formatter was changed.

The CI workflow runs the native xUnit host on `ubuntu-latest` without a culture override. The
explicit local `en-US` setting is a reproducible test environment, **not a claim that a CI job's
actual culture was observed**. Both the default-culture failure receipt and the explicit-culture
success receipt are retained.

### Review boundary regressions

PR review identified two additional boundaries in the shared regeneration path. Four cases ran
against the unchanged PR head `96c157e3`: the present-definition/null-configuration warm-cache
control passed, while the missing-definition case and both real mesh source-read cases failed.
The missing definition still produced a cache hit. The direct reader and the include fallback
both left the subscribing caller holding `system-security` after `Subscribe` returned.

The amended cases require a present null configuration to keep reusing the original artifact,
but an unresolved definition to fall through to the existing fresh compiler. The real mesh read
cases also assert the returned node and caller identity inside the result callback, so restoring
an identity by preventing the read from working cannot pass. They invoke the existing private
reader boundary through reflection without exposing a new production API. The amended focused
selection passed **54/54** after a strict Release build with zero warnings or errors. The
amended full compiler suite passed **849/849** with explicit `en-US`, zero errors, skips or unrun
tests. The earlier default-culture failure and pre-review receipts above remain historical evidence.

Reproduce with the normal SDK fixture:

```bash
dotnet build test/MeshWeaver.Compiler.Pipeline.Test/MeshWeaver.Compiler.Pipeline.Test.csproj -c Release -warnaserror
dotnet test test/MeshWeaver.Compiler.Pipeline.Test/MeshWeaver.Compiler.Pipeline.Test.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~CompileCacheInputFreshnessTest|FullyQualifiedName~CompileSourceReadIdentityTest|FullyQualifiedName~CacheHitStampsTheSameRecordTest|FullyQualifiedName~ContentKeyReevaluationTest|FullyQualifiedName~GeneratedInputIdentityTest' --logger trx
cd test/MeshWeaver.Compiler.Pipeline.Test/bin/Release/net10.0
dotnet MeshWeaver.Compiler.Pipeline.Test.dll -culture en-US -trx compiler-suite-en-US.trx -showLiveOutput -longRunning 60
```

## The local sighting and what it does not prove

The local 8339 Store preview supplied a separate production-code observation: a new native test
class was present in a PDB-matched emitted source, while its test-area registration remained old.
A later forced Compile at 04:43:49 returned a disk-cache hit on the 04:41 DLL and failed while
pinning an unloading assembly context. A separately authorized normal retry later loaded the same
DLL; its PDB still showed the old registration while the live/current and compiled source stamps
agreed. The cache/compiler files were byte-identical at that preview's core `508aeb6` and the
regression baseline `6231c4da`.

The executed regression proves the cache's stale-byte/current-stamp mismatch. It does **not**
establish which import/query boundary first supplied the preview's mixed snapshot, nor which
operation caused the three failed scan-pin attempts. The unloading failure is a separate lifecycle
investigation. Recycle unloads contexts but deliberately retains disk artifacts, so its success
alone cannot certify source freshness.

Original local evidence is retained at `/private/tmp/roadmap-compile-cache-investigation`
(`before.trx`, `before-test.log`, `before-receipt.json`, exact baseline source copies and subsequent
test receipts). The preview's PDB and activity receipts remain under
`/private/tmp/coupon-input-preview-adoption-plan/post-cta-retry-pdb`. These paths are local evidence,
not dependencies of the committed test. No deployment or production verification is claimed here.
