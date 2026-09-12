# Localized refusals — which surface is translated, and which one is not

A write boundary answers a caller with **two** surfaces at once, and only one of them is read by a
person:

| surface | who reads it | language |
|---|---|---|
| `CreateOrUpdateNodeResponse.Error` (and every `*Response.Error`) | **code** — services, importers, installers, webhooks | **English, always** |
| the `ActivityLog` travelling on the same response | a **viewer**, at render time | **the viewer's** |

This page states the rule, the evidence for it, and the mechanism that keeps the two from drifting.

## The rule

**A `*Response.Error` is a WIRE FIELD and stays English. The localized surface is the activity
transcript.**

Measured before deciding: every consumer of `CreateOrUpdateNodeResponse.Error` under `src/` is a
service — `ModuleDiscoveryService`, `PackageInstaller`, `GitHubCredentialService`,
`GitHubSyncService`, `IssueService`, `GitHubWebhookProcessor`, `StaticRepoImporter` (which folds it
into an exception message), `NodeCopyHelper`. Not one of them is a viewer surface. Translating that
field would translate a value that code reads, matches and logs — the same reason
[wire identifiers are never translated](../Localization) — while giving no viewer a German sentence.

The corollary is the part that is easy to get wrong: **because the wire field is not the localized
surface, a boundary that answers ONLY with an `Error` and no `ActivityLog` cannot be localized at
all.** `CreateNodeResponse.Fail(error, reason)` is exactly that shape today; it carries no `Log`, so
its refusals have nowhere to put a key. Giving the create leg a transcript is the prerequisite for
localizing it, not an afterthought.

## Why the key has to travel WITH the sentence

A refusal is frequently composed by a shared guard and logged by a handler several frames away:

```
AccessAssignmentGuard.ScopeRefusal   ─┐
NodeTypeResolution.Rejection         ─┼─►  HandleCreateOrUpdateNodeRequest.PostFail  ─►  ActivityLog
NodeTypeResolution.ProbeFailed       ─┘
```

Handing the handler only the finished English string throws the key away at the first frame, and
**the handler cannot recover it** — it does not know WHICH branch of the guard fired, so it can
name neither the key nor the arguments. That is precisely how the upsert handler's failure surface
ended up English-only while its success surface was localized (MeshWeaver#3917): `PostOk` took a
`(logLine, logKey)` pair, `PostFail` took a bare string, and there was no parameter to thread a key
through even for the sites that knew one.

`LocalizableText` is that pair, kept together end to end:

```csharp
public sealed record LocalizableText(string English, string? Key, ...)
{
    public static LocalizableText Keyed(string english, string key, params (string, object?)[] args);
    public static LocalizableText Verbatim(string english);
    public LogMessage ToLogMessage(LogLevel level);
}
```

- `English` is BOTH the wire `Error` and the stored `LogMessage.Message` fallback. They are written
  from one value, so they cannot drift.
- `Keyed` is the normal case. Write the English as the sentence the key renders in English.
- `Verbatim` is the deliberate escape hatch, and **its name is the review signal**. There is no
  implicit conversion from `string`: an unkeyed sentence is the defect this type exists to prevent,
  so producing one has to be spelled out.

## When `Verbatim` is right

Only when the text is **upstream output this process did not author** — an exception message, a
Roslyn diagnostic, a descendant hub's own refusal. A catalog cannot carry those, and a template
reduced to a lone `{detail}` placeholder is a translation of nothing.

🚨 **Where the sentence AROUND such a fragment is ours, key the sentence and pass the fragment as an
ARGUMENT.** `Inner CreateNode faulted: {error}` translates; `{error}` does not. That is the same
shape `activity.dataUpdate.streamUpdateFailed` has always had.

## The two guards, and the blind spot between them

`UnkeyedActivityLogMessageRatchetGuard` counts `new LogMessage(…)` constructions with no chained
`.WithKey(…)`, per file, ratcheting DOWN only. A boundary migrated to `LocalizableText` **no longer
constructs one** — `ToLogMessage` does, and it always chains `.WithKey` (blank keys are a documented
no-op, so `Verbatim` needs no branch). So that guard can no longer see a new English-only refusal
added at a migrated boundary.

`NoNewVerbatimLocalizableTextIsIntroduced`, in the same file, closes it: it ratchets
`LocalizableText.Verbatim(` per file on the same DOWN-only rule, and asserts `Keyed` is used
somewhere so it cannot pass on a tree that abandoned the type.

**Migrating a boundary therefore moves an allowance from one ratchet to the other; it must not
create a new one in either.**

## Checklist for migrating a boundary

1. Find every refusal the boundary posts, including the ones composed elsewhere. Fix them all —
   keying some of them is worse than keying none, because a viewer cannot tell a half-translated
   dialog from a translation bug.
2. Give shared guards a `LocalizableText`-returning member beside the existing string builder;
   keep the string builder (deleting a public member is a
   [cross-repo pair](../CrossRepoPairGate) event) and make the keyed member call it, so the two
   spellings are one sentence.
3. Add every key to **both** `strings.en.json` and `strings.de.json`, naming the **same
   placeholders** in each — a translation that drops `{path}` deletes the only per-occurrence
   information the line carries.
4. Lower the file's entry in `test/UnkeyedActivityLogMessages.allow` and `TotalBudget` with it.
5. Hand over the React mirror sync: `npm run sync:i18n -- --ref <merged core sha>`. Core merges
   first; the mirror's guard compares against a PINNED core commit, so adding a key in core reddens
   nothing and leaves the mirror silently stale.

## Related

- [Localization](../Localization) — the three lookup shapes and the ownership rule.
- [ChromeAndContentLanguage](../ChromeAndContentLanguage) — whose language chrome follows inside
  authored content.
- [ControlsThatCannotFail](../ControlsThatCannotFail) — the sibling rule for instruments that must be
  able to say "I did not check".
