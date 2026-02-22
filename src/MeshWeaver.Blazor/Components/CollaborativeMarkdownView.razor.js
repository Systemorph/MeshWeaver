import { ensureHighlightJs, initializeThemeUpdates } from '../highlightUtils.js';

let _containerEl = null;
let _resizeHandler = null;
let _resizeObserver = null;
let _mutationObserver = null;
let _repositionTimer = null;
let _dotNetRef = null;
let _commentButton = null;
let _contentMouseUpHandler = null;
let _documentMouseDownHandler = null;

export function init(containerEl) {
    _containerEl = containerEl;
    if (!containerEl) return;

    const contentEl = containerEl.querySelector('.collab-md-content');
    if (contentEl) {
        contentEl.addEventListener('click', function (event) {
            const annotationEl = event.target.closest('[data-comment-id], [data-change-id]');
            if (annotationEl) {
                event.stopPropagation();
                const id = annotationEl.dataset.commentId || annotationEl.dataset.changeId;
                if (id) {
                    highlightAnnotation(id);
                }
            }
        });
    }

    _resizeHandler = () => positionCards();
    window.addEventListener('resize', _resizeHandler);

    // Watch sidebar for card size changes (e.g. when async LayoutArea content loads)
    const sidebarEl = containerEl.querySelector('.collab-md-sidebar');
    if (sidebarEl) {
        observeSidebar(sidebarEl);
    }

    // Initial positioning after layout settles
    requestAnimationFrame(() => positionCards());
}

function debouncedReposition() {
    if (_repositionTimer) clearTimeout(_repositionTimer);
    _repositionTimer = setTimeout(() => positionCards(), 50);
}

function observeSidebar(sidebarEl) {
    // ResizeObserver: fires when any card changes size (content loaded)
    if (typeof ResizeObserver !== 'undefined') {
        _resizeObserver = new ResizeObserver(() => debouncedReposition());
        // Observe the sidebar itself and all current cards
        _resizeObserver.observe(sidebarEl);
        sidebarEl.querySelectorAll('.annotation-card').forEach(c => _resizeObserver.observe(c));
    }

    // MutationObserver: fires when new cards are added (so we can observe them too)
    _mutationObserver = new MutationObserver((mutations) => {
        let changed = false;
        for (const m of mutations) {
            if (m.addedNodes.length > 0 || m.removedNodes.length > 0) {
                changed = true;
                // Observe any newly added cards
                if (_resizeObserver) {
                    for (const node of m.addedNodes) {
                        if (node.nodeType === 1) {
                            if (node.classList?.contains('annotation-card')) {
                                _resizeObserver.observe(node);
                            }
                            node.querySelectorAll?.('.annotation-card')?.forEach(c => _resizeObserver.observe(c));
                        }
                    }
                }
            }
        }
        if (changed) debouncedReposition();
    });
    _mutationObserver.observe(sidebarEl, { childList: true, subtree: true });
}

/**
 * Aligns each sidebar card vertically with its corresponding inline annotation span.
 * Cards are pushed down via margin-top to match the span's vertical position,
 * but never overlap — each card stays below the previous one.
 */
export function positionCards() {
    if (!_containerEl) return;

    const contentEl = _containerEl.querySelector('.collab-md-content');
    const sidebarEl = _containerEl.querySelector('.collab-md-sidebar');
    if (!contentEl || !sidebarEl) return;

    // Lazily set up sidebar observers (sidebar may not exist at init time)
    if (!_mutationObserver) {
        observeSidebar(sidebarEl);
    }

    const cards = Array.from(sidebarEl.querySelectorAll('.annotation-card'));
    if (cards.length === 0) return;

    // Reset all margins so we measure from natural flow positions
    cards.forEach(c => c.style.marginTop = '');

    // Force reflow to get accurate positions after reset
    void sidebarEl.offsetHeight;

    const gap = 6;

    cards.forEach(card => {
        const id = getAnnotationId(card);
        if (!id) return;

        const span = contentEl.querySelector(`[data-change-id="${id}"], [data-comment-id="${id}"]`);
        if (!span) return;

        const spanTop = span.getBoundingClientRect().top;
        const cardTop = card.getBoundingClientRect().top;

        // Push card down to align with its inline span
        // If the card is already below the span (pushed by previous cards), leave it
        const delta = spanTop - cardTop;
        if (delta > gap) {
            card.style.marginTop = delta + 'px';
        }
    });
}

export async function highlightCodeBlocks(contentEl) {
    if (!contentEl) return;

    const codeElements = contentEl.querySelectorAll('pre code');
    if (codeElements.length === 0) return;

    await ensureHighlightJs();
    initializeThemeUpdates();

    codeElements.forEach(el => hljs.highlightElement(el));
}

export function highlightAnnotation(annotationId) {
    // Remove previous highlights
    document.querySelectorAll('.annotation-active').forEach(el => el.classList.remove('annotation-active'));
    document.querySelectorAll('.annotation-card.active').forEach(el => el.classList.remove('active'));

    // Highlight marker in content
    const container = _containerEl?.querySelector('.collab-md-content')
        || document.querySelector('.markdown-annotations-container');
    if (container) {
        const marker = container.querySelector(`[data-comment-id="${annotationId}"]`) ||
                       container.querySelector(`[data-change-id="${annotationId}"]`);
        if (marker) {
            marker.classList.add('annotation-active');
            marker.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }

    // Highlight card in side panel
    const card = document.querySelector(`.annotation-for-${annotationId}`);
    if (card) {
        card.classList.add('active');
        card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
}

/**
 * Enables comment-from-selection: shows a floating "Comment" button when the user
 * selects text in the content area. On click, sends the selected text back to Blazor.
 */
export function enableCommentSelection(containerEl, dotNetRef) {
    _dotNetRef = dotNetRef;
    const contentEl = containerEl?.querySelector('.collab-md-content');
    if (!contentEl) return;

    // Create floating comment button
    _commentButton = document.createElement('button');
    _commentButton.className = 'comment-selection-btn';
    _commentButton.innerHTML = '&#128172; Comment';
    _commentButton.style.display = 'none';
    containerEl.appendChild(_commentButton);

    _contentMouseUpHandler = (e) => {
        // Small delay to let the selection finalize
        setTimeout(() => {
            const sel = window.getSelection();
            const selectedText = sel?.toString().trim();
            if (!selectedText || selectedText.length === 0) {
                _commentButton.style.display = 'none';
                return;
            }

            // Ensure selection is within our content area
            if (!contentEl.contains(sel.anchorNode) || !contentEl.contains(sel.focusNode)) {
                _commentButton.style.display = 'none';
                return;
            }

            // Position the button near the end of the selection
            const range = sel.getRangeAt(0);
            const rect = range.getBoundingClientRect();
            const containerRect = containerEl.getBoundingClientRect();

            _commentButton.style.display = 'block';
            _commentButton.style.top = (rect.bottom - containerRect.top + 4) + 'px';
            _commentButton.style.left = (rect.right - containerRect.left) + 'px';
        }, 10);
    };

    _commentButton.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const sel = window.getSelection();
        const selectedText = sel?.toString().trim();
        if (!selectedText || !_dotNetRef) return;

        _commentButton.style.display = 'none';

        try {
            await _dotNetRef.invokeMethodAsync('OnCommentFromSelection', selectedText);
        } catch (err) {
            console.error('Error creating comment from selection:', err);
        }

        sel.removeAllRanges();
    });

    _documentMouseDownHandler = (e) => {
        if (_commentButton && !_commentButton.contains(e.target)) {
            _commentButton.style.display = 'none';
        }
    };

    contentEl.addEventListener('mouseup', _contentMouseUpHandler);
    document.addEventListener('mousedown', _documentMouseDownHandler);
}

export function dispose() {
    if (_resizeHandler) {
        window.removeEventListener('resize', _resizeHandler);
        _resizeHandler = null;
    }
    if (_resizeObserver) {
        _resizeObserver.disconnect();
        _resizeObserver = null;
    }
    if (_mutationObserver) {
        _mutationObserver.disconnect();
        _mutationObserver = null;
    }
    if (_repositionTimer) {
        clearTimeout(_repositionTimer);
        _repositionTimer = null;
    }
    if (_documentMouseDownHandler) {
        document.removeEventListener('mousedown', _documentMouseDownHandler);
        _documentMouseDownHandler = null;
    }
    if (_contentMouseUpHandler) {
        const contentEl = _containerEl?.querySelector('.collab-md-content');
        if (contentEl) contentEl.removeEventListener('mouseup', _contentMouseUpHandler);
        _contentMouseUpHandler = null;
    }
    if (_commentButton) {
        _commentButton.remove();
        _commentButton = null;
    }
    _dotNetRef = null;
    _containerEl = null;
}

function getAnnotationId(card) {
    for (const cls of card.classList) {
        if (cls.startsWith('annotation-for-')) {
            return cls.substring('annotation-for-'.length);
        }
    }
    return null;
}
