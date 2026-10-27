---
Name: Test Log Levels — Warning Only
Category: Architecture
Description: Unit and CI test runs write Warning and above to the shared test-log sinks, with no Information allow-list. Why a floor in the sink rather than a default in appsettings, what it measured, how a test that needs a lower-level line captures it, and how to read Information locally.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h10"/><path d="M4 18h6"/></svg>
---

# Test Log Levels — Warning Only

**Unit and CI test runs write `Warning` and above, and nothing below it** (maintainer, 2026-10-26:
*"unit and CI test runs: warning only"*). There is no Information allow-list for tests: a test that
needs a lower-level line captures that logger itself.

## Where the rule lives

In the two shared sinks of `test/MeshWeaver.Fixture` — `XUnitLogger` (every host built by a
`HostBuilder`: an Orleans silo, its client, every hub inside them) and `XUnitFileLogger` (the
`TestBase` container). Both resolve their level from configuration as before and then apply
`TestLogLevel.Resolve`, which raises anything below `Warning` to `Warning`.

**Why a floor and not a default.** The test `appsettings.json` files were already `Warning`, and
the sinks still wrote Information, because both fell back to Information when the host they served
had no `Logging` section — and an Orleans `TestCluster` silo host has none. Measured on core CI run
40411029765, shard 0: **18,434** `info,` lines in a 39,050-line job log, against **56** at Warning
or above (`MeshWeaver.Data.Workspace` content-type registration 3,704, `[QUIESCE-START]` 1,957 and
`[QUIESCE-OK]` 1,935 per hub disposal, `Orleans.Runtime.SiloOptionsLogger` 840). A default can be
overridden by any of the dozens of places a test host is composed; a floor in the sink cannot.

The plugin gate (`tools/MeshWeaver.PluginTester`) follows the same rule: its Information raises
(`bake-seed`, the recycle-attribution categories) are on only under `MW_LOG_LEVEL=Information` or
lower. Measured on Plugins CI run 40409265629, gate shard 1/3: 774 of 2,943 job-log lines were those
raises on a green run.

## A test that asserts on a lower-level line

It never goes through the shared sinks. It adds its own `ILoggerProvider` and opts the one category
in with a `LoggerFilterRule` (or `AddFilter<TProvider>`) — the shape
`LateNackReenqueueCorrelationTest` already uses. The floor does not touch a provider the test owns,
and `ILoggerFactory.CreateLogger(x).IsEnabled(level)` still answers true when any provider wants the
level.

The fault sink (`TestTraceLog.AppendFault`) is Warning-and-above by construction and unaffected.

## Reading Information locally

Set `MW_TEST_LOG_LEVEL=Debug` (or `Information`) in your shell before `dotnet test`. Never in CI and
never in a committed file — that would be the allow-list the rule forbids.

The explicit setting REPLACES the floor rather than feeding it (`TestLogLevel.Resolve(configured,
override)` returns the override when one is set), and it also reaches the `LoggerFactory`:
`TestLogLevel.ApplyOverride` adds a category-less rule for each shared sink's provider, and a
provider-specific rule beats every provider-less rule in the factory's selection — so the shared
`appsettings.json`'s `"MeshWeaver": "Warning"` cannot filter Debug out before the sink sees it. A
floor alone could only ever raise the level, which left the switch a no-op (Copilot on #7285);
`TestLogLevelOverrideTest` pins both halves. For the plugin gate the
switch is `MW_LOG_LEVEL`, as before.

## Production is a different rule

A deployed portal is `Default: Warning` plus a short, declared Information allow-list of the
categories operators read (the image's `appsettings.json` in MeshWeaver.Plugins, held by
`ShippedLoggingLevelsGuard`). Tests have no operator reading them live, so they get no allow-list.
