// Monaco Editor View JavaScript module
const editorState = new Map();

// Debounce utility function
function debounce(fn, delay) {
    let timeoutId = null;
    return function (...args) {
        if (timeoutId) {
            clearTimeout(timeoutId);
        }
        return new Promise((resolve) => {
            timeoutId = setTimeout(async () => {
                timeoutId = null;
                const result = await fn.apply(this, args);
                resolve(result);
            }, delay);
        });
    };
}

// Add global styles for suggest widget (needed because FixedOverflowWidgets renders outside component)
(function addSuggestWidgetStyles() {
    if (document.getElementById('monaco-suggest-styles')) return;

    const style = document.createElement('style');
    style.id = 'monaco-suggest-styles';
    style.textContent = `
        /* Target suggest widget in overflow widgets container */
        .overflowingContentWidgets .suggest-widget,
        .monaco-editor .suggest-widget {
            width: 550px !important;
            min-width: 550px !important;
            max-width: 550px !important;
        }

        /* Force the list to use full width */
        .overflowingContentWidgets .suggest-widget .monaco-list,
        .monaco-editor .suggest-widget .monaco-list {
            width: 100% !important;
        }

        .overflowingContentWidgets .suggest-widget .monaco-list-rows,
        .monaco-editor .suggest-widget .monaco-list-rows {
            width: 100% !important;
        }

        /* Each row needs full width */
        .overflowingContentWidgets .suggest-widget .monaco-list-row,
        .monaco-editor .suggest-widget .monaco-list-row {
            width: 100% !important;
            display: flex !important;
        }

        /* The suggestion content */
        .overflowingContentWidgets .suggest-widget .suggest-icon,
        .monaco-editor .suggest-widget .suggest-icon {
            flex-shrink: 0 !important;
        }

        .overflowingContentWidgets .suggest-widget .contents,
        .monaco-editor .suggest-widget .contents {
            flex: 1 !important;
            min-width: 0 !important;
            display: flex !important;
            align-items: center !important;
        }

        .overflowingContentWidgets .suggest-widget .main,
        .monaco-editor .suggest-widget .main {
            flex: 1 !important;
            min-width: 0 !important;
            display: flex !important;
            align-items: center !important;
        }

        /* Label and description inline */
        .overflowingContentWidgets .suggest-widget .left,
        .monaco-editor .suggest-widget .left {
            flex: 1 !important;
            display: flex !important;
            align-items: center !important;
            min-width: 0 !important;
        }

        .overflowingContentWidgets .suggest-widget .monaco-icon-label,
        .monaco-editor .suggest-widget .monaco-icon-label {
            flex: 1 !important;
            display: flex !important;
            align-items: center !important;
        }

        .overflowingContentWidgets .suggest-widget .monaco-icon-label-container,
        .monaco-editor .suggest-widget .monaco-icon-label-container {
            flex: 1 !important;
            display: flex !important;
            align-items: center !important;
        }

        .overflowingContentWidgets .suggest-widget .monaco-icon-name-container,
        .monaco-editor .suggest-widget .monaco-icon-name-container {
            flex-shrink: 0 !important;
        }

        .overflowingContentWidgets .suggest-widget .monaco-icon-description-container,
        .monaco-editor .suggest-widget .monaco-icon-description-container {
            flex: 1 !important;
            margin-left: 12px !important;
            opacity: 0.7 !important;
            white-space: nowrap !important;
            overflow: hidden !important;
            text-overflow: ellipsis !important;
        }
    `;
    document.head.appendChild(style);
})();

export function initEditor(editorId, placeholder, dotNetRef, codeEditMode = false) {
    const container = document.getElementById(editorId);
    if (!container) {
        console.error('Container not found:', editorId);
        return;
    }

    // Store state for this editor
    editorState.set(editorId, {
        dotNetRef: dotNetRef,
        completionConfig: null,
        completionDisposable: null,
        codeEditMode: codeEditMode
    });

    // Add placeholder styling
    updatePlaceholder(editorId, placeholder);

    // Get the monaco editor instance
    const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
    if (editorInstance) {
        // Handle content changes for placeholder
        editorInstance.onDidChangeModelContent(() => {
            const value = editorInstance.getValue();
            updatePlaceholderVisibility(editorId, !value);
        });

        // Handle Enter key - in code edit mode, Enter inserts newline; in chat mode, Enter submits
        const state = editorState.get(editorId);
        if (!state?.codeEditMode) {
            // Chat input mode: Enter submits, Shift+Enter inserts newline
            editorInstance.onKeyDown(async (e) => {
                // Check for Enter key without modifiers
                if (e.keyCode === monaco.KeyCode.Enter && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
                    // Check if suggest widget is visible
                    // States: 0=Hidden, 1=Loading, 2=Empty, 3=Open, 4=Frozen, 5=Details
                    // Use > 0 to handle undefined/null case (where !== 0 would wrongly be true)
                    const suggestController = editorInstance.getContribution('editor.contrib.suggestController');
                    const suggestState = suggestController?.model?.state;
                    const isSuggestVisible = typeof suggestState === 'number' && suggestState > 0;

                    if (!isSuggestVisible) {
                        e.preventDefault();
                        e.stopPropagation();
                        const currentState = editorState.get(editorId);
                        if (currentState?.dotNetRef) {
                            try {
                                await currentState.dotNetRef.invokeMethodAsync('HandleSubmit');
                            } catch (err) {
                                console.error('Error calling HandleSubmit:', err);
                            }
                        }
                    }
                }
            });

            // Allow Shift+Enter for new line in chat mode
            editorInstance.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.Enter, () => {
                editorInstance.trigger('keyboard', 'type', { text: '\n' });
            });
        }
        // In code edit mode, Enter naturally inserts newlines (default Monaco behavior)

        // Force layout after initialization
        setTimeout(() => {
            editorInstance.layout();
        }, 100);
    } else {
        console.error('Editor instance not found for', editorId);
    }
}

function updatePlaceholder(editorId, placeholder) {
    const container = document.getElementById(editorId);
    if (!container) return;

    // Create or update placeholder element
    let placeholderEl = container.querySelector('.monaco-placeholder');
    if (!placeholderEl) {
        placeholderEl = document.createElement('div');
        placeholderEl.className = 'monaco-placeholder';
        placeholderEl.style.cssText = `
            position: absolute;
            top: 8px;
            left: 10px;
            color: var(--neutral-foreground-hint, #605e5c);
            pointer-events: none;
            font-size: 14px;
            font-family: var(--body-font, "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif);
            z-index: 1;
        `;
        container.style.position = 'relative';
        container.appendChild(placeholderEl);
    }
    placeholderEl.textContent = placeholder;

    // Check initial visibility
    const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
    if (editorInstance) {
        const value = editorInstance.getValue();
        updatePlaceholderVisibility(editorId, !value);
    }
}

function updatePlaceholderVisibility(editorId, show) {
    const container = document.getElementById(editorId);
    if (!container) return;

    const placeholderEl = container.querySelector('.monaco-placeholder');
    if (placeholderEl) {
        placeholderEl.style.display = show ? 'block' : 'none';
    }
}

export function registerCompletionProvider(editorId, config) {
    const state = editorState.get(editorId);
    if (!state) {
        console.error('No state found for editor:', editorId);
        return;
    }

    // Parse config
    const triggerCharacters = config?.triggerCharacters || [];
    const useAsync = config?.useAsync || false;
    let items = [];
    if (Array.isArray(config?.items)) {
        items = config.items;
    } else if (config?.items && typeof config.items === 'object') {
        items = Object.values(config.items);
    }

    state.completionConfig = { triggerCharacters, items, useAsync };
    state.isCompletionPending = false;

    // Dispose previous provider if exists
    if (state.completionDisposable) {
        state.completionDisposable.dispose();
        state.completionDisposable = null;
    }

    // Only register if we have items or async mode, and trigger characters
    if (!useAsync && items.length === 0) {
        return;
    }
    if (triggerCharacters.length === 0) {
        return;
    }

    // Build trigger character set for regex (not used directly anymore, but kept for reference)
    const escapedTriggers = triggerCharacters.map(c => c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('');

    // Create debounced async fetch function (150ms delay)
    const debouncedFetch = debounce(async (query) => {
        if (!state.dotNetRef) {
            return [];
        }
        try {
            state.isCompletionPending = true;
            const result = await state.dotNetRef.invokeMethodAsync('GetAsyncCompletions', query);
            return result;
        } catch (e) {
            console.error('Error fetching async completions:', e);
            return [];
        } finally {
            state.isCompletionPending = false;
        }
    }, 150);

    // Register new completion provider
    // Note: Monaco registers providers globally per language, so we need to check
    // if this request is for our specific editor instance
    state.completionDisposable = monaco.languages.registerCompletionItemProvider('plaintext', {
        triggerCharacters: triggerCharacters,
        provideCompletionItems: async (model, position) => {
            // Check if this model belongs to our editor
            const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
            if (!editorInstance || editorInstance.getModel() !== model) {
                // This completion request is not for our editor, skip it
                return { suggestions: [] };
            }

            const currentState = editorState.get(editorId);
            const isAsync = currentState?.completionConfig?.useAsync || false;

            const textUntilPosition = model.getValueInRange({
                startLineNumber: position.lineNumber,
                startColumn: 1,
                endLineNumber: position.lineNumber,
                endColumn: position.column
            });

            let fullQuery;
            let matchLength;

            // Check if we're after a trigger character (e.g., @, /)
            // We need different handling for @ vs / triggers:
            // - @ can be followed by paths with slashes: @agent/Name, @content/path/file
            // - / is only a trigger at word boundary (for commands like /agent)

            // First try to match @ followed by path (including slashes)
            let triggerMatch = textUntilPosition.match(/@([\w\-\./]+)?$/);

            // If no @ match, try / but only if it's at word boundary (start or after space)
            if (!triggerMatch) {
                triggerMatch = textUntilPosition.match(/(?:^|\s)\/([\w\-\.]+)?$/);
                if (triggerMatch) {
                    // Adjust match to not include the leading space
                    const fullMatch = triggerMatch[0];
                    const slashIndex = fullMatch.indexOf('/');
                    triggerMatch[0] = fullMatch.substring(slashIndex);
                    // triggerMatch[1] stays the same (the capture group)
                }
            }

            if (!triggerMatch) {
                return { suggestions: [] };
            }

            const triggerChar = triggerMatch[0].charAt(0);
            const afterTrigger = triggerMatch[1] || '';

            // Include trigger char in query for server to determine context
            // For @ prefix: could be @agent/Name, @model/Name, or just @something
            fullQuery = triggerChar + afterTrigger;
            matchLength = triggerMatch[0].length;

            // Calculate range to replace (from trigger/prefix to current position)
            const range = new monaco.Range(
                position.lineNumber,
                position.column - matchLength,
                position.lineNumber,
                position.column
            );

            let currentItems;

            if (isAsync) {
                // Async mode: fetch from server with debounce (send full query including trigger char)
                currentItems = await debouncedFetch(fullQuery);
            } else {
                // Sync mode: filter locally
                const allItems = currentState?.completionConfig?.items || [];
                const searchTermLower = searchTerm.toLowerCase();
                currentItems = allItems.filter(item =>
                    item && item.label &&
                    (item.label.toLowerCase().includes(searchTermLower) ||
                    (item.description && item.description.toLowerCase().includes(searchTermLower)))
                );
            }

            if (!Array.isArray(currentItems)) {
                return { suggestions: [] };
            }

            const suggestions = currentItems.map((item) => ({
                label: {
                    label: item.label,
                    description: item.description || ''
                },
                // Use item.kind if provided, otherwise default to Text (0)
                // Monaco CompletionItemKind values match our enum: Module=8, File=16, Function=2, Text=0
                kind: typeof item.kind === 'number' ? item.kind : monaco.languages.CompletionItemKind.Text,
                insertText: item.insertText || item.label,
                range: range,
                // Show category as detail (appears on the right side)
                detail: item.category || item.detail || '',
                // filterText tells Monaco what to match against the user's input
                filterText: item.label,
                // sortText: category first, then label for grouping
                sortText: (item.category || 'zzz') + '_' + item.label.toLowerCase()
            }));

            return { suggestions };
        }
    });
}

export function isAutocompleteVisible(editorId) {
    // Check if async completion is pending
    const state = editorState.get(editorId);
    if (state?.isCompletionPending) {
        return true;
    }

    // Check DOM for visible suggest widgets (works with FixedOverflowWidgets)
    const suggestWidgets = document.querySelectorAll('.monaco-editor .suggest-widget, .overflowingContentWidgets .suggest-widget');
    for (const widget of suggestWidgets) {
        const style = window.getComputedStyle(widget);
        if (style.display !== 'none' && style.visibility !== 'hidden' && widget.offsetParent !== null) {
            return true;
        }
    }

    // Fallback: check editor contribution state
    const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
    if (editorInstance) {
        try {
            const contribution = editorInstance.getContribution('editor.contrib.suggestController');
            if (contribution && contribution.widget && contribution.widget.value) {
                const widgetState = contribution.widget.value.state;
                // States: 0=Hidden, 1=Loading, 2=Empty, 3=Open, 4=Frozen, 5=Details
                if (widgetState >= 1 && widgetState <= 5) {
                    return true;
                }
            }
        } catch (e) {
            // Ignore errors
        }
    }

    return false;
}

export function dispose(editorId) {
    const state = editorState.get(editorId);
    if (state) {
        if (state.completionDisposable) {
            state.completionDisposable.dispose();
        }
        editorState.delete(editorId);
    }
}
