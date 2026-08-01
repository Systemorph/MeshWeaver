/**
 * Select-to-comment affordance, factored out of CollaborativeMarkdownView so ANY rendered
 * content can offer it (a social post, an HTML block, a composed stack) — not just markdown.
 *
 * Instance-scoped by design: the markdown view's original version kept the button and the
 * handlers in MODULE-level variables, so a second commentable area on the same page overwrote
 * the first one's state and only the last one could be disposed. `enable` returns a handle that
 * owns everything it created; `disable(handle)` removes exactly that.
 */

/**
 * Shows a floating "Comment" button while text is selected inside the content element, and hands
 * the selection back to Blazor on click.
 *
 * @param containerEl the positioned wrapper the button is placed in (position: relative)
 * @param contentSelector CSS selector of the text region within it; null ⇒ the container itself
 * @param dotNetRef Blazor object reference receiving OnCommentFromSelection(text, start, end)
 * @returns a handle to pass to {@link disable}, or null when the content element is absent
 */
export function enable(containerEl, contentSelector, dotNetRef) {
    const contentEl = contentSelector ? containerEl?.querySelector(contentSelector) : containerEl;
    if (!contentEl || !dotNetRef) return null;

    const button = document.createElement('button');
    button.className = 'comment-selection-btn';
    button.innerHTML = '&#128172; Comment';
    button.style.display = 'none';
    containerEl.appendChild(button);

    const onMouseUp = () => {
        // Let the selection finalize before measuring it.
        setTimeout(() => {
            const sel = window.getSelection();
            const selectedText = sel?.toString().trim();
            if (!selectedText) {
                button.style.display = 'none';
                return;
            }
            // Only a selection wholly inside OUR content offers the button — otherwise every
            // commentable area on the page would light up for a selection in any of them.
            if (!contentEl.contains(sel.anchorNode) || !contentEl.contains(sel.focusNode)) {
                button.style.display = 'none';
                return;
            }

            const rect = sel.getRangeAt(0).getBoundingClientRect();
            const containerRect = containerEl.getBoundingClientRect();
            const btnWidth = 90;
            button.style.display = 'block';
            const centerX = (rect.left + rect.right) / 2 - containerRect.left - btnWidth / 2;
            button.style.top = (rect.top - containerRect.top - 32) + 'px';
            button.style.left = Math.max(0, centerX) + 'px';
        }, 10);
    };

    const onClick = async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const sel = window.getSelection();
        const selectedText = sel?.toString().trim();
        if (!selectedText) return;
        button.style.display = 'none';

        // Send the selection plus its leading/trailing word fragments. The server matches those
        // against the node's SOURCE text — mapping rendered-HTML offsets back to source is not
        // solvable in general, and the fragments make the match robust to markup in between.
        const words = selectedText.split(/\s+/);
        const startFragment = words.slice(0, Math.min(5, words.length)).join(' ');
        const endFragment = words.slice(Math.max(0, words.length - 5)).join(' ');

        try {
            await dotNetRef.invokeMethodAsync(
                'OnCommentFromSelection', selectedText, startFragment, endFragment);
        } catch (err) {
            console.error('Error creating comment from selection:', err);
        }
        sel.removeAllRanges();
    };

    const onDocumentMouseDown = (e) => {
        if (!button.contains(e.target)) button.style.display = 'none';
    };

    button.addEventListener('click', onClick);
    contentEl.addEventListener('mouseup', onMouseUp);
    document.addEventListener('mousedown', onDocumentMouseDown);

    return { button, contentEl, onMouseUp, onDocumentMouseDown };
}

/** Removes the button and every listener {@link enable} attached. Safe on a null handle. */
export function disable(handle) {
    if (!handle) return;
    handle.contentEl?.removeEventListener('mouseup', handle.onMouseUp);
    document.removeEventListener('mousedown', handle.onDocumentMouseDown);
    handle.button?.remove();
}
