using MeshWeaver.Layout;

namespace MeshWeaver.GitSync;

/// <summary>
/// The pieces the GitSync tabs (GitHub Sync, GitHub Issues, Code workspace) and the GitHub action
/// page share, composed from the platform's controls only — no markup, no colours of their own.
/// The click outcome lines are <see cref="ActionNotice"/>s written with
/// <see cref="ActionNoticeExtensions.ShowNotice"/> and rendered by
/// <see cref="ActionNoticeExtensions.NoticeView"/>.
/// </summary>
internal static class GitSyncViews
{
    /// <summary>A section heading inside a tab.</summary>
    /// <param name="title">The heading text.</param>
    /// <returns>An H3 label.</returns>
    internal static UiControl Section(string title) =>
        Controls.H3(title).WithStyle("margin: 20px 0 8px 0;");

    /// <summary>An explanatory line, an empty state or a loading placeholder.</summary>
    /// <param name="text">The text.</param>
    /// <returns>A <see cref="Controls.Hint"/> label.</returns>
    internal static UiControl Hint(string text) => Controls.Hint(text);

    /// <summary>A short line that may carry <c>code</c>, <em>emphasis</em> or a link — Markdown, never HTML.</summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <returns>A Markdown control.</returns>
    internal static UiControl Note(string markdown) => Controls.Markdown(markdown);
}
