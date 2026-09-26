---
Name: The Profile Page
Category: Documentation
Description: The owner's profile page at /{user}/EditProfile — picture upload, display name, sign-in email, language and time zone, bio, links, showcase — and the extension point modules use to add sections to it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="8" r="4"/><path d="M4 21v-1a7 7 0 0 1 14 0v1"/></svg>
---

Every user has a **profile page** at `/{user}/EditProfile` where they change how they appear to everyone else: their picture, their display name, their language and time zone, a short bio, their links and the showcase of pinned work. Visitors see the read-only result at `/{user}/Profile`.

It is reached from three places:

- the **Profile** entry in the avatar menu at the top right of the portal (the Blazor portal's user menu);
- **Edit profile** in the home page's node menu (beside **View public profile**);
- the **Edit profile** button on the public profile, shown to the owner only.

The page is owner-only: it requires `Update` on the user node, so a visitor who opens the URL gets the access-denied view.

---

# What is on the page

| Section | What it edits | Where the value lives |
|---|---|---|
| **Picture** | Upload, replace or remove the profile picture | the user node's `Icon` (`content:picture/…`), bytes in the node's own `content` collection |
| **Basics** | Display name · sign-in email (read-only) · language · time zone | `MeshNode.Name` · `User.Email` · `User.Locale` · `User.TimeZoneId` |
| **Bio** | A short markdown bio | `User.Bio` |
| **Links** | One markdown link per line | `User.Links` |
| **Showcase** | Pinned cards, unpinned in place | `User.PinnedPaths` |
| *contributed* | Whatever a module adds — e.g. the Store's subscription | the module's own nodes |

Every field is bound **directly to the user node** ([Data Binding](/Doc/GUI/DataBinding)): there is no `/data` copy, no save subscription and no Save button — a change is written through `GetMeshNodeStream(path).Update(...)` as it happens. The page itself only re-renders when its *shape* changes (the node appears, or the pins change), so a field being typed in is never rebuilt under the cursor.

The **display name** is the node's own `Name`, which is what the header, mentions, cards and the public profile show. It is written when the field loses focus, not per keystroke. The **language** and **time zone** pickers are the same fields and the same control as *Settings → Preferences*, so the two surfaces cannot disagree.

---

# The picture

The picture is a `NodeImageUploadControl(nodePath)` — a platform control, usable for any node whose icon is an image. Its view reads the picture from the node stream and writes through `NodeImageUpload` (`MeshWeaver.ContentCollections`):

- **Upload / Replace** — the file is written into the node's **default `content` collection** under the managed folder `picture/` with a fresh, server-generated name, then `Icon` is set to `content:picture/{name}`. The user's file name is never used, so nothing in it can traverse or collide, and the new URL defeats a browser cache that still holds the old picture. The previous managed picture is deleted.
- **Remove** — `Icon` is cleared and the managed file is deleted.
- **Accepted:** PNG, JPEG, GIF, WebP, up to 5 MB. SVG is refused: an uploaded SVG is served from the portal's own origin and can carry script.
- **Order of effects:** bytes first, then the node update (which enforces `Update` on the node). If the update is refused, the file just written is deleted again before the error surfaces.
- **Hand-set icons are safe:** only a file inside `picture/` is ever deleted. An icon the owner pointed at another file of theirs (via *Settings → Metadata*) is cleared on Remove but its file is left alone.

`content:` references resolve to the access-controlled `/api/content/{nodePath}/…` URL. `MeshNodeImageHelper.ResolvePictureUrl(icon, nodePath)` returns that URL for a picture and `null` for an emoji, inline SVG or glyph name, so an avatar falls back to initials instead of a broken image. The header avatar, the public profile and node cards and mentions all use the node's icon, so the picture appears everywhere at once.

---

# Adding a section from a module

A module adds a section **without core referencing the module**, the same way it adds a settings tab ([Settings Page](/Doc/GUI/SettingsPage)): a provider carried on the **user hub's configuration**, contributed onto the User node type with `AddNodeHubContribution`.

```csharp
// In the module's own configuration (e.g. its ConfigureDefaultNodeHub).
config.AddNodeHubContribution(UserNodeType.NodeType, userHub => userHub
    .AddProfileSections(new ProfileSectionDefinition(
        Id: "subscription",
        Title: "Subscription",
        ContentBuilder: (host, userNode) => BuildSubscription(host, userNode),
        Order: 100)
    { TitleKey = "store.profileSubscription" }));
```

| Type | Namespace | Role |
|---|---|---|
| `ProfileSectionDefinition(Id, Title, ContentBuilder, Order = 0, RequiredPermission = Update, Icon = null) { TitleKey }` | `MeshWeaver.Mesh` | One section |
| `ProfileSectionBuilder(LayoutAreaHost host, MeshNode? userNode) → UiControl` | `MeshWeaver.Mesh` | Builds the section body; the page supplies the heading |
| `ProfileSectionProvider(LayoutAreaHost host, RenderingContext ctx) → IObservable<IReadOnlyList<ProfileSectionDefinition>>` | `MeshWeaver.Mesh` | A reactive provider, for sections that depend on a live check |
| `AddProfileSections(params ProfileSectionDefinition[])` / `AddProfileSections(params ProfileSectionProvider[])` | `MeshWeaver.Graph.Configuration` | Registration on the user hub |

The rules the page applies:

- **Placement.** Contributed sections render **after** the built-in ones, lowest `Order` first, each under an `H3` heading and with the stable id `profile-section-{Id}`.
- **Identity.** Two registrations with the same `Id`: the first wins.
- **Permission.** Filtered against the viewer's *latest* effective permissions on the user node. The default, `Update`, means owner-only — right for anything personal such as a subscription. `Permission.None` shows the section to every viewer of the page.
- **Language.** `Title` is English; set `TitleKey` to a catalog key and supply both `en` and `de` values — the page resolves it for each viewer.
- **Liveness.** `userNode` is a snapshot taken when the page last changed shape. A section that shows live node values binds to `host.Workspace.GetMeshNodeStream()` (or any other stream) inside its body — never `Task`, never `.Take(1)` on a live binding.
- **Failure.** A builder that throws is logged and replaced by a visible, localized *"This section could not be displayed"* message; the rest of the page still renders. A provider that errors contributes nothing.

---

# Source

- Page and sections: `src/MeshWeaver.Graph/UserActivityLayoutAreas.cs` (`EditProfile`, `BuildProfileEditor`)
- Extension point: `src/MeshWeaver.Mesh.Contract/ProfileSectionDefinition.cs`, `src/MeshWeaver.Graph/Configuration/ProfileSectionsExtensions.cs`
- Picture: `src/MeshWeaver.Graph/NodeImageUploadControl.cs`, `src/MeshWeaver.ContentCollections/NodeImageUpload.cs`
- Tests: `test/MeshWeaver.Graph.Test/UserProfilePageTest.cs`, `test/MeshWeaver.Graph.Test/ProfilePictureRoundTripTest.cs`
- The Blazor views (the picture control's view, the avatar menu) live in MeshWeaver.Plugins.
