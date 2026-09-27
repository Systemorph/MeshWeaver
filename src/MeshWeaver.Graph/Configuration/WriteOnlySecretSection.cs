using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>What a <see cref="SecretSectionVerbs.Generate"/> verb produced.</summary>
/// <param name="Value">The new value, returned ONCE so it can be shown: it appears once in a dialog, is
/// never stored in the layout, and is never logged. NULL means the value is NEVER SHOWN to anyone,
/// e.g. a key minted inside an operator Job for two ends that both read the vault. The dialog then
/// shows only the fingerprint and the note.</param>
/// <param name="Status">The resulting status.</param>
/// <param name="Note">Optional markdown shown in the dialog, e.g. where a vault copy was filed.</param>
public sealed record SecretGenerated(string? Value, SecretStatus Status, string? Note = null);

/// <summary>
/// What a write-only secret surface may DO. Each verb is optional; a verb left null has no button.
/// Every verb runs on the server, checks the caller's rights itself, and returns a
/// <see cref="SecretStatus"/> — never a value, except <see cref="Generate"/>, which returns the
/// value it minted exactly once.
/// </summary>
public sealed record SecretSectionVerbs
{
    /// <summary>Set or replace the value with what the person pasted.</summary>
    public Func<string, IObservable<SecretStatus>>? Save { get; init; }

    /// <summary>Generate a strong value on the server and return it once.</summary>
    public Func<IObservable<SecretGenerated>>? Generate { get; init; }

    /// <summary>Keep the value but stop using it.</summary>
    public Func<IObservable<SecretStatus>>? Disable { get; init; }

    /// <summary>Use a disabled value again.</summary>
    public Func<IObservable<SecretStatus>>? Enable { get; init; }

    /// <summary>Delete the value (recoverable). The section asks for confirmation first.</summary>
    public Func<IObservable<SecretStatus>>? Delete { get; init; }

    /// <summary>Undo a deletion within its recovery window.</summary>
    public Func<IObservable<SecretStatus>>? Recover { get; init; }
}

/// <summary>Everything a <see cref="WriteOnlySecretSection"/> renders.</summary>
/// <param name="Id">Unique on the page; namespaces the section's layout data.</param>
/// <param name="Status">The live status. The ONLY thing the section reads back.</param>
public sealed record SecretSectionSpec(string Id, IObservable<SecretStatus> Status)
{
    /// <summary>A heading, already localized.</summary>
    public string? Title { get; init; }

    /// <summary>An explanation in markdown, already localized.</summary>
    public string? Intro { get; init; }

    /// <summary>The input's label, already localized. Defaults to a generic one.</summary>
    public string? InputLabel { get; init; }

    /// <summary>The input's placeholder, already localized.</summary>
    public string? Placeholder { get; init; }

    /// <summary>The Generate button's label, already localized (e.g. "Issue key"). Defaults to "Generate".</summary>
    public string? GenerateLabel { get; init; }

    /// <summary>The Generate button's label once a value is present (e.g. "Rotate key"). Defaults to <see cref="GenerateLabel"/>.</summary>
    public string? RegenerateLabel { get; init; }

    /// <summary>The Disable button's label, already localized (e.g. "Revoke key").</summary>
    public string? DisableLabel { get; init; }

    /// <summary>Extra live markdown under the status, e.g. the last use. Never a value.</summary>
    public IObservable<string>? Detail { get; init; }

    /// <summary>Whether the viewer may change the secret. Without it, only the status shows. Defaults to true.</summary>
    public IObservable<bool>? CanChange { get; init; }

    /// <summary>The verbs.</summary>
    public SecretSectionVerbs Verbs { get; init; } = new();

    /// <summary>Extra controls placed beside the verb buttons (e.g. a "Test connection" button).</summary>
    public ImmutableList<UiControl> ExtraButtons { get; init; } = [];
}

/// <summary>
/// The platform's ONE write-only secret control (policy <c>secrets-write-only-entry</c>): a password
/// box and the verbs of <see cref="SecretSectionVerbs"/>, over a live <see cref="SecretStatus"/>.
///
/// <para>🚨 <b>Nothing is ever read back.</b> The section shows whether a value is set, its state, its
/// fingerprint, and who set it and when. The password box's buffer is cleared the moment it is
/// read. A generated value is shown ONCE, in a dialog that warns it will not be shown again, and
/// is gone from the layout when the dialog closes.</para>
///
/// <para>Composed from the framework's controls (Stack, Markdown, a password TextField, Button,
/// Dialog) and data-bound to the section's own transient layout data. Nothing here stores a
/// value: the verbs do, on the server.</para>
/// </summary>
public static class WriteOnlySecretSection
{
    /// <summary>The form-buffer field the password box binds to.</summary>
    public const string ValueField = "value";

    /// <summary>The layout-data key of a section's password buffer.</summary>
    public static string FormId(string id) => $"{id}-secret-form";

    /// <summary>The layout-data key of a section's one-line result.</summary>
    public static string ResultId(string id) => $"{id}-secret-result";

    /// <summary>Renders one write-only secret section.</summary>
    public static UiControl Render(LayoutAreaHost host, SecretSectionSpec spec)
    {
        host.UpdateData(FormId(spec.Id), new Dictionary<string, object?> { [ValueField] = "" });
        host.UpdateData(ResultId(spec.Id), "");

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        if (!string.IsNullOrEmpty(spec.Title))
            stack = stack.WithView(Controls.H3(spec.Title!), "Title");
        if (!string.IsNullOrEmpty(spec.Intro))
            stack = stack.WithView(Controls.Markdown(spec.Intro!), "Intro");

        stack = stack.WithView((h, _) => spec.Status
            .Select(s => (UiControl?)Controls.Markdown(StatusMarkdown(s, (k, a) => h.Localize(k, a))))
            .StartWith((UiControl?)Controls.Markdown("")), "Status");
        if (spec.Detail is not null)
            stack = stack.WithView((_, _) => spec.Detail
                .Select(d => (UiControl?)Controls.Markdown(d ?? ""))
                .StartWith((UiControl?)Controls.Markdown("")), "Detail");

        var canChange = spec.CanChange ?? Observable.Return(true);
        stack = stack.WithView((h, _) => canChange
            .CombineLatest(spec.Status, (may, status) => (may, status))
            .Select(t => (UiControl?)(t.may ? Editor(h, spec, t.status) : Controls.Stack))
            .StartWith((UiControl?)Controls.Stack), "Editor");

        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId(spec.Id))
            .Select(msg => (UiControl?)(string.IsNullOrEmpty(msg) ? Controls.Stack : Controls.Markdown(msg)))
            .StartWith((UiControl?)Controls.Stack), "Result");
        return stack;
    }

    private static UiControl Editor(LayoutAreaHost host, SecretSectionSpec spec, SecretStatus status)
    {
        var verbs = spec.Verbs;
        var editor = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        if (verbs.Save is not null)
            editor = editor.WithView(Controls.Text(new JsonPointerReference(ValueField)) with
            {
                DataContext = LayoutAreaReference.GetDataPointer(FormId(spec.Id)),
                Label = spec.InputLabel ?? host.Localize("secret.inputLabel"),
                Placeholder = spec.Placeholder ?? host.Localize("secret.inputPlaceholder"),
                Password = true,
            }, "Input");

        var buttons = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; gap: 8px; flex-wrap: wrap;");
        if (verbs.Save is { } save)
            buttons = buttons.WithView(Controls.Button(host.Localize("common.save"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    Save(ctx.Host, spec.Id, save);
                    return Task.CompletedTask;
                }), "Save");
        if (verbs.Generate is { } generate)
            buttons = buttons.WithView(Controls.Button(
                    status.Present && !status.Deleted
                        ? spec.RegenerateLabel ?? spec.GenerateLabel ?? host.Localize("secret.generate")
                        : spec.GenerateLabel ?? host.Localize("secret.generate"))
                .WithAppearance(verbs.Save is null ? Appearance.Accent : Appearance.Neutral)
                .WithClickAction(ctx =>
                {
                    Generate(ctx.Host, spec.Id, generate);
                    return Task.CompletedTask;
                }), "Generate");
        if (verbs.Disable is { } disable && status.Present && status.Enabled != false && !status.Deleted)
            buttons = buttons.WithView(Verb(host, spec.Id, spec.DisableLabel ?? host.Localize("secret.disable"), disable), "Disable");
        if (verbs.Enable is { } enable && status.Enabled == false && !status.Deleted)
            buttons = buttons.WithView(Verb(host, spec.Id, host.Localize("secret.enable"), enable), "Enable");
        if (verbs.Delete is { } delete && status.Present && !status.Deleted)
            buttons = buttons.WithView(Controls.Button(host.Localize("common.delete"))
                .WithClickAction(ctx =>
                {
                    ConfirmDelete(ctx.Host, spec.Id, delete);
                    return Task.CompletedTask;
                }), "Delete");
        if (verbs.Recover is { } recover && status.Deleted)
            buttons = buttons.WithView(Verb(host, spec.Id, host.Localize("secret.recover"), recover), "Recover");
        var index = 0;
        foreach (var extra in spec.ExtraButtons)
            buttons = buttons.WithView(extra, "Extra" + index++);
        return editor.WithView(buttons, "Buttons");
    }

    private static UiControl Verb(LayoutAreaHost host, string id, string label, Func<IObservable<SecretStatus>> verb) =>
        Controls.Button(label).WithClickAction(ctx =>
        {
            Run(ctx.Host, id, verb);
            return Task.CompletedTask;
        });

    private static void Run(LayoutAreaHost host, string id, Func<IObservable<SecretStatus>> verb)
    {
        host.UpdateData(ResultId(id), host.Localize("secret.working"));
        Observable.Defer(verb).Take(1).Subscribe(
            _ => host.UpdateData(ResultId(id), host.Localize("secret.done")),
            ex => host.UpdateData(ResultId(id), host.Localize("secret.failed", Describe(ex, host.ViewerLocale()))));
    }

    /// <summary>
    /// Save: one bounded read of the buffer, then the buffer is CLEARED whatever happens, so the
    /// value never lingers in the layout data. The value is trimmed: a value pasted from an e-mail
    /// usually carries a trailing newline, and a secret never ends in whitespace on purpose.
    /// </summary>
    private static void Save(LayoutAreaHost host, string id, Func<string, IObservable<SecretStatus>> save)
    {
        host.UpdateData(ResultId(id), host.Localize("secret.working"));
        host.GetDataStream<Dictionary<string, object?>>(FormId(id))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(20))
            .Select(form => (form is not null && form.TryGetValue(ValueField, out var v) ? v?.ToString() : null)?.Trim() ?? "")
            // Cleared on EVERY termination of the read — a value, a timeout or a fault — so the
            // typed secret never lingers in the layout data.
            .Finally(() => host.UpdateData(FormId(id), new Dictionary<string, object?> { [ValueField] = "" }))
            .SelectMany(value => value.Length == 0
                ? Observable.Throw<SecretStatus>(new InvalidOperationException(host.Localize("secret.empty")))
                : save(value).Take(1))
            .Subscribe(
                status => host.UpdateData(ResultId(id), host.Localize("secret.saved", status.Fingerprint ?? "")),
                ex => host.UpdateData(ResultId(id), host.Localize("secret.failed", Describe(ex, host.ViewerLocale()))));
    }

    /// <summary>Generate: the value comes back ONCE and is shown in a dialog; closing it removes it from the layout.</summary>
    private static void Generate(LayoutAreaHost host, string id, Func<IObservable<SecretGenerated>> generate)
    {
        host.UpdateData(ResultId(id), host.Localize("secret.working"));
        Observable.Defer(generate).Take(1).Subscribe(
            generated =>
            {
                host.UpdateData(ResultId(id), host.Localize("secret.saved", generated.Status.Fingerprint ?? ""));
                var body = Controls.Stack.WithStyle("gap: 10px;");
                // A null value is never shown to anyone: only its fingerprint and the note.
                if (!string.IsNullOrEmpty(generated.Value))
                    body = body
                        .WithView(Controls.Markdown(host.Localize("secret.shownOnce")), "Warning")
                        .WithView(Controls.Markdown($"```text\n{generated.Value}\n```"), "Value");
                body = body.WithView(Controls.Markdown(host.Localize("secret.fingerprintLine", generated.Status.Fingerprint ?? "")), "Fingerprint");
                if (!string.IsNullOrEmpty(generated.Note))
                    body = body.WithView(Controls.Markdown(generated.Note!), "Note");
                var close = Controls.Button(host.Localize("common.close"))
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
                        return Task.CompletedTask;
                    });
                host.UpdateArea(DialogControl.DialogArea,
                    Controls.Dialog(body, host.Localize("secret.generatedTitle")).WithSize("M").WithActions(close));
            },
            ex => host.UpdateData(ResultId(id), host.Localize("secret.failed", Describe(ex, host.ViewerLocale()))));
    }

    private static void ConfirmDelete(LayoutAreaHost host, string id, Func<IObservable<SecretStatus>> delete)
    {
        var actions = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; gap: 8px;")
            .WithView(Controls.Button(host.Localize("common.delete"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    Run(ctx.Host, id, delete);
                    return Task.CompletedTask;
                }), "Confirm")
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
                    return Task.CompletedTask;
                }), "Cancel");
        host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(host.Localize("secret.confirmDelete")), host.Localize("common.delete"))
                .WithSize("S").WithActions(actions));
    }

    /// <summary>The status as markdown: state, fingerprint, who and when. Never a value. Pure over the localizer.</summary>
    public static string StatusMarkdown(SecretStatus status, Func<string, object?[], string> localize)
    {
        var lines = new List<string>();
        if (status.Deleted)
            lines.Add(localize("secret.status.deleted", [Stamp(status.RecoverableUntil)]));
        else if (!status.Present)
            lines.Add(localize("secret.status.absent", []));
        else
            lines.Add(status.Fingerprint == SecretFingerprint.Withheld || status.Fingerprint is null
                ? localize("secret.status.presentWithheld", [])
                : localize("secret.status.present", [status.Fingerprint]));
        // Also for a disabled key with no value of its own: a tombstone over a mounted copy.
        if (status.Enabled == false && !status.Deleted)
            lines.Add(localize("secret.status.disabled", []));
        if (status.SetAt is not null || status.SetBy is not null)
            lines.Add(localize("secret.status.setBy", [Stamp(status.SetAt), status.SetBy ?? "?", SourceLabel(status.Source, localize)]));
        if (status.Expires is { } expires)
            lines.Add(localize("secret.status.expires", [Stamp(expires)]));
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Why a verb failed, in <paramref name="locale"/>: a refusal from the secret store is keyed
    /// (<see cref="InstanceSecretException"/>) and rendered in the viewer's language; any other
    /// failure is upstream text (a transport error, the mesh's own words), shown verbatim.
    /// </summary>
    public static string Describe(Exception error, string? locale) =>
        error is InstanceSecretException refusal ? refusal.Text.Localize(locale) : error.Message;

    /// <summary>
    /// A <see cref="SecretStatus.Source"/> for display: the known values localized, an unknown one
    /// shown as it is (the vocabulary is open), and an absent one as "?". Pure over the localizer.
    /// </summary>
    public static string SourceLabel(string? source, Func<string, object?[], string> localize) => source switch
    {
        SecretSources.Paste => localize("secret.source.paste", []),
        SecretSources.Generate => localize("secret.source.generate", []),
        null or "" => "?",
        _ => source,
    };

    /// <summary>An instant as <c>yyyy-MM-dd HH:mm UTC</c>, culture-invariant (never the thread culture).</summary>
    public static string Stamp(DateTimeOffset? instant) =>
        instant is { } i ? i.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "?";
}

/// <summary>One row of a <see cref="SecretInventorySection"/> table: names, states and fingerprints — never a value.</summary>
/// <param name="Name">The secret's name.</param>
/// <param name="State">Set, not set, disabled or deleted, localized.</param>
/// <param name="Fingerprint">The fingerprint, or empty.</param>
/// <param name="SetBy">Who set it.</param>
/// <param name="SetAt">When it was set.</param>
/// <param name="Expires">When it expires, or empty.</param>
public sealed record SecretInventoryRow(string Name, string State, string Fingerprint, string SetBy, string SetAt, string Expires);

/// <summary>
/// A list of secrets with the write-only verbs on each: a table of every secret's status, then one
/// <see cref="WriteOnlySecretSection"/> per secret. The same control whether the list comes from a
/// vault inventory or from <see cref="InstanceSecrets"/>.
/// </summary>
public static class SecretInventorySection
{
    /// <summary>Renders the inventory.</summary>
    /// <param name="host">The rendering host.</param>
    /// <param name="id">Unique on the page.</param>
    /// <param name="rows">The live list of statuses.</param>
    /// <param name="verbsFor">The verbs for one secret, by name.</param>
    /// <param name="canChange">Whether the viewer may change secrets. Defaults to true.</param>
    public static UiControl Render(LayoutAreaHost host, string id, IObservable<ImmutableList<SecretStatus>> rows,
        Func<string, SecretSectionVerbs> verbsFor, IObservable<bool>? canChange = null)
    {
        var shared = rows;
        return Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;")
            .WithView((h, _) => shared.Select(list => (UiControl?)Controls.DataGrid(list.Select(s => Row(s, (k, a) => h.Localize(k, a))).ToArray())
                    .WithColumn(new PropertyColumnControl<string> { Property = "name" }.WithTitle(h.Localize("secret.col.name")))
                    .WithColumn(new PropertyColumnControl<string> { Property = "state" }.WithTitle(h.Localize("secret.col.state")))
                    .WithColumn(new PropertyColumnControl<string> { Property = "fingerprint" }.WithTitle(h.Localize("secret.col.fingerprint")))
                    .WithColumn(new PropertyColumnControl<string> { Property = "setBy" }.WithTitle(h.Localize("secret.col.setBy")))
                    .WithColumn(new PropertyColumnControl<string> { Property = "setAt" }.WithTitle(h.Localize("secret.col.setAt")))
                    .WithColumn(new PropertyColumnControl<string> { Property = "expires" }.WithTitle(h.Localize("secret.col.expires")))), "Table")
            .WithView((h, _) => shared
                .Select(list => list.Select(s => s.Name).ToImmutableList())
                .DistinctUntilChanged(names => string.Join("\n", names))
                .Select(names => (UiControl?)names.Aggregate(
                    Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;"),
                    (stack, name) => stack.WithView(WriteOnlySecretSection.Render(h,
                        new SecretSectionSpec($"{id}-{names.IndexOf(name)}",
                            shared.Select(list => list.FirstOrDefault(s => s.Name == name) ?? new SecretStatus { Name = name }))
                        {
                            Title = name,
                            Verbs = verbsFor(name),
                            CanChange = canChange,
                        })))), "Sections");
    }

    /// <summary>The table row for one status. Pure over the localizer.</summary>
    public static SecretInventoryRow Row(SecretStatus s, Func<string, object?[], string> localize) => new(
        s.Name,
        s.Deleted ? localize("secret.state.deleted", [])
            : !s.Present ? localize("secret.state.absent", [])
            : s.Enabled == false ? localize("secret.state.disabled", [])
            : localize("secret.state.set", []),
        s.Fingerprint ?? "",
        s.SetBy ?? "",
        s.SetAt is null ? "" : WriteOnlySecretSection.Stamp(s.SetAt),
        s.Expires is null ? "" : WriteOnlySecretSection.Stamp(s.Expires));
}
