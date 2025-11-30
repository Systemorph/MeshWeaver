// Monaco Editor View JavaScript module
const editorState = new Map();

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

export function initEditor(editorId, placeholder, dotNetRef) {
    const container = document.getElementById(editorId);
    if (!container) {
        console.error('Container not found:', editorId);
        return;
    }

    // Store state for this editor
    editorState.set(editorId, {
        dotNetRef: dotNetRef,
        completionConfig: null,
        completionDisposable: null
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

        // Handle Enter key for submit - use addAction for better context handling
        editorInstance.addAction({
            id: 'chat-submit',
            label: 'Submit Message',
            keybindings: [monaco.KeyCode.Enter],
            // Only run when suggest widget is NOT visible
            precondition: '!suggestWidgetVisible',
            run: async () => {
                const state = editorState.get(editorId);
                if (state?.dotNetRef) {
                    await state.dotNetRef.invokeMethodAsync('HandleSubmit');
                }
            }
        });

        // Allow Shift+Enter for new line
        editorInstance.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.Enter, () => {
            editorInstance.trigger('keyboard', 'type', { text: '\n' });
        });

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
    let items = [];
    if (Array.isArray(config?.items)) {
        items = config.items;
    } else if (config?.items && typeof config.items === 'object') {
        items = Object.values(config.items);
    }

    state.completionConfig = { triggerCharacters, items };

    // Dispose previous provider if exists
    if (state.completionDisposable) {
        state.completionDisposable.dispose();
        state.completionDisposable = null;
    }

    // Only register if we have items and trigger characters
    if (items.length === 0 || triggerCharacters.length === 0) {
        return;
    }

    // Build trigger character set for regex
    const escapedTriggers = triggerCharacters.map(c => c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('');

    // Register new completion provider
    state.completionDisposable = monaco.languages.registerCompletionItemProvider('plaintext', {
        triggerCharacters: triggerCharacters,
        provideCompletionItems: (model, position) => {
            const currentState = editorState.get(editorId);
            const currentItems = currentState?.completionConfig?.items || [];

            if (!Array.isArray(currentItems)) {
                return { suggestions: [] };
            }

            const textUntilPosition = model.getValueInRange({
                startLineNumber: position.lineNumber,
                startColumn: 1,
                endLineNumber: position.lineNumber,
                endColumn: position.column
            });

            // Check if we're after a trigger character (e.g., @)
            const triggerMatch = textUntilPosition.match(new RegExp(`[${escapedTriggers}](\\w*)$`));
            if (!triggerMatch) {
                return { suggestions: [] };
            }

            const searchTerm = triggerMatch[1].toLowerCase();

            // Filter items based on search term
            const filteredItems = currentItems.filter(item =>
                item && item.label &&
                (item.label.toLowerCase().includes(searchTerm) ||
                (item.description && item.description.toLowerCase().includes(searchTerm)))
            );

            // Calculate range to replace (from trigger char to current position)
            const range = new monaco.Range(
                position.lineNumber,
                position.column - triggerMatch[0].length,
                position.lineNumber,
                position.column
            );

            // Get the actual trigger character from the match
            const triggerChar = triggerMatch[0].charAt(0);

            const suggestions = filteredItems.map((item) => ({
                label: {
                    label: item.label,
                    description: item.description || ''
                },
                kind: monaco.languages.CompletionItemKind.User,
                insertText: item.insertText || item.label,
                range: range,
                // Show category as detail (appears on the right side)
                detail: item.category || '',
                // filterText tells Monaco what to match against the user's input
                filterText: triggerChar + item.label,
                // sortText: category first, then label for grouping
                sortText: (item.category || 'zzz') + '_' + item.label.toLowerCase()
            }));

            console.log('[Monaco] Returning', suggestions.length, 'suggestions for text:', textUntilPosition);
            return { suggestions };
        }
    });
}

export function isAutocompleteVisible(editorId) {
    const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
    if (!editorInstance) return false;

    // Check if suggest widget is visible
    try {
        const contribution = editorInstance.getContribution('editor.contrib.suggestController');
        if (contribution && contribution.widget && contribution.widget.value) {
            return contribution.widget.value.state === 3; // SuggestWidget.State.Open = 3
        }
    } catch (e) {
        // Fallback method
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
