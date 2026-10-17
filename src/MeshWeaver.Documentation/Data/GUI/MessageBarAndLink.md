---
Name: Message Bars & Links
Category: Documentation
Description: The platform's banner (MessageBarControl — info, success, warning, error, with an optional action) and inline text link (LinkControl — safe hrefs, new tab, download), each rendered live. Use them instead of hand-built HTML.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M12 9v4"/><path d="M12 16h.01"/></svg>
---

# Message Bars & Links

Two small controls that hand-built HTML used to stand in for. A banner is a `MessageBarControl`,
never a `Controls.Html("<div style=…>")` box. A link in running text is a `LinkControl`, never
`Controls.Html("<a href=…>")`. Both work the same in every renderer (Blazor, React, React Native),
in both themes and with assistive technology. Every value is a literal or a
[data-binding](../DataBinding) pointer.

---

# Message Bar

`Controls.MessageBar(body)` draws a banner that tells the reader something about the page: it is
read-only, an account is not connected, a save worked, a sync failed. `WithIntent` takes one of the
`MessageBarIntent` constants, `Info` (the default), `Success`, `Warning` or `Error`. `WithTitle` adds
a bold lead-in.

```csharp --render MessageBarIntents --show-code
Controls.Stack
    .WithVerticalGap("8px")
    .WithView(Controls.MessageBar("This node is read-only for you.").WithTitle("Read-only"))
    .WithView(Controls.MessageBar("Pushed 3 files to main.").WithIntent(MessageBarIntent.Success))
    .WithView(Controls.MessageBar("The repository has no default branch yet.").WithIntent(MessageBarIntent.Warning))
    .WithView(Controls.MessageBar("The token was refused by GitHub.")
        .WithIntent(MessageBarIntent.Error)
        .WithTitle("Sync failed"))
```

**Accessibility.** The renderer gives the bar a live-region role that follows its intent:
`role="alert"` for `Warning` and `Error`, which a screen reader reads at once, and `role="status"`
for everything else, which it reads when it is free. `MessageBarIntent.AriaRoleOf(intent)` is that
rule, and every renderer calls it or mirrors it. So choose the intent by how urgent the message is,
not by the colour you want.

**An unknown intent.** `MessageBarIntent` is an open vocabulary. A renderer that meets a value it does
not know draws the neutral `Info` styling with `role="status"`, and names the value in its log. It
never makes an unknown value look like a warning or an error.

## An action

`WithAction(control)` puts one control beside the message. Use a `Button` with its own click action
or `WithNavigateToHref`, or a `Link`. The action renders as the bar's own child area, `{area}/Action`,
so its click reaches its own handler exactly as it would anywhere else on the page, with the button's
pending state included ([Buttons: Pending State](../ButtonPendingState)).

```csharp --render MessageBarAction --show-code
Controls.MessageBar("Connect your GitHub account to sync this space.")
    .WithIntent(MessageBarIntent.Warning)
    .WithTitle("Not connected")
    .WithAction(Controls.Button("Connect").WithNavigateToHref("/auth/github/connect"))
```

The body is plain text. When the message itself needs a link or emphasis, give it an action, or put
a `Markdown` control next to the bar. Never put markup in the body.

---

# Link

`Controls.Link(text, href)` is an inline text link for body content: "open the pull request",
"download the report", "read the guide". Put it in a horizontal `Stack` next to labels, or use it as a
message bar's action.

```csharp --render LinkKinds --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("16px")
    .WithWrap(true)
    .WithView(Controls.Link("Data binding", "/Doc/GUI/DataBinding"))
    .WithView(Controls.Link("Fluent UI", "https://www.fluentui-blazor.net/").WithOpenInNewTab())
    .WithView(Controls.Link("Download the sample", "/static/sample.csv").WithDownload("sample.csv"))
    .WithView(Controls.Link("Write to support", "mailto:support@example.com").WithTooltip("Opens your mail app"))
```

| Builder | What it does |
|---|---|
| `WithOpenInNewTab()` / `WithTarget("_blank")` | Opens in a new browser tab. |
| `WithDownload(true)` / `WithDownload("name.pdf")` | Downloads instead of navigating, under the server's file name or the one given. |
| `WithTooltip(text)` | Hover text, rendered as the element's `title`. Pass a localized string. |

Write a mesh path as an absolute path (`/Doc/GUI`). Never write `@/…` in an `href`: that form is for
markdown links only.

## Href safety

The renderer applies the rules in `LinkHrefs`, not the code that builds the control, so a bound
`href` is held to the same rules as a literal one:

- **Only `http`, `https`, `mailto` and `tel`, or a relative address.** Any other scheme is refused:
  `javascript:`, `vbscript:`, `data:` and `file:`. A refused link is drawn as plain text, never as a
  link and never rewritten into a different address. The scheme is read the way a browser reads it.
  Whitespace and control characters are dropped first, so `java` followed by a tab and `script:` is refused too.
- **An external address, or one that opens in another tab, carries `rel="noopener noreferrer"`.** The
  page it opens gets no handle on the portal's window and no referrer.

```csharp --render LinkSafety --show-code
Controls.Stack
    .WithView(Controls.Label($"https://example.com → safe: {LinkHrefs.IsSafe("https://example.com")}, rel: {LinkHrefs.RelFor("https://example.com", null)}"))
    .WithView(Controls.Label($"/Doc/GUI → safe: {LinkHrefs.IsSafe("/Doc/GUI")}, rel: {LinkHrefs.RelFor("/Doc/GUI", null) ?? "(none)"}"))
    .WithView(Controls.Label($"javascript:alert(1) → safe: {LinkHrefs.IsSafe("javascript:alert(1)")}"))
```

## Which link control?

| You want | Use |
|---|---|
| A link in running text | `Controls.Link` |
| An entry in a side menu or navigation | `Controls.NavLink` ([Navigation Menus](../Navigation)) |
| An action that looks like a button | `Controls.Button(...).WithNavigateToHref(...)` |
| Links inside prose you are writing anyway | `Controls.Markdown` |

---

# See Also

- [Badges, Icons & Status](../DisplayControls): badges, icons, progress bars
- [Container Controls](../ContainerControl): stacks, tabs, toolbars, splitters
- [Data Binding](../DataBinding): binding any value to a JSON pointer
