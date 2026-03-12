// Monaco Editor View JavaScript module
const editorState = new Map();

// =============================================================================
// Monaco Theme Synchronization
// =============================================================================

let themeCallbackRegistered = false;

// Update all Monaco editors to match the app theme
function syncMonacoTheme(effectiveTheme) {
    const monacoTheme = effectiveTheme === 'dark' ? 'vs-dark' : 'vs';

    // Check if Monaco is available
    if (typeof monaco !== 'undefined' && monaco.editor) {
        // Set the global Monaco theme - this affects all editors
        monaco.editor.setTheme(monacoTheme);
    }
}

// Detect theme from DOM when themeHandler is not available
function detectThemeFromDOM() {
    // FluentDesignTheme sets data-theme on document.body
    const bodyTheme = document.body?.getAttribute('data-theme');
    if (bodyTheme) {
        return bodyTheme;
    }
    // Also check documentElement as fallback
    const htmlTheme = document.documentElement.getAttribute('data-theme');
    if (htmlTheme) {
        return htmlTheme;
    }
    // Check for dark/fluent-dark class on body or html
    if (document.body?.classList.contains('dark') ||
        document.body?.classList.contains('fluent-dark') ||
        document.documentElement.classList.contains('dark') ||
        document.documentElement.classList.contains('fluent-dark')) {
        return 'dark';
    }
    // Fallback to system preference
    if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
        return 'dark';
    }
    return 'light';
}

// Register theme change callback (called from initEditor when Monaco is ready)
function ensureThemeCallbackRegistered() {
    if (themeCallbackRegistered) return;

    if (typeof window.themeHandler !== 'undefined' && window.themeHandler.registerThemeChangeCallback) {
        window.themeHandler.registerThemeChangeCallback((effectiveTheme, isDark) => {
            syncMonacoTheme(effectiveTheme);
        });
        themeCallbackRegistered = true;

        // Apply current theme immediately
        const currentTheme = window.themeHandler.getEffectiveTheme();
        syncMonacoTheme(currentTheme);
    } else {
        // Fallback: detect theme from DOM and apply
        const currentTheme = detectThemeFromDOM();
        syncMonacoTheme(currentTheme);

        // Also listen for system theme changes
        if (window.matchMedia) {
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
                // Only use system preference if no explicit theme is set
                if (!document.documentElement.getAttribute('data-theme')) {
                    syncMonacoTheme(e.matches ? 'dark' : 'light');
                }
            });
        }

        // Set up MutationObservers to watch for theme attribute changes on both body and html
        const observer = new MutationObserver((mutations) => {
            for (const mutation of mutations) {
                if (mutation.type === 'attributes' &&
                    (mutation.attributeName === 'data-theme' || mutation.attributeName === 'class')) {
                    const theme = detectThemeFromDOM();
                    syncMonacoTheme(theme);
                }
            }
        });

        // Observe both body and documentElement for theme changes
        if (document.body) {
            observer.observe(document.body, { attributes: true, attributeFilter: ['data-theme', 'class'] });
        }
        observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class'] });

        themeCallbackRegistered = true;
    }
}

// =============================================================================

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

        /* Each row - let Monaco calculate height, just ensure proper layout */
        .overflowingContentWidgets .suggest-widget .monaco-list-row,
        .monaco-editor .suggest-widget .monaco-list-row {
            width: 100% !important;
            box-sizing: border-box !important;
        }

        /* Primary label (node name) - bold */
        .overflowingContentWidgets .suggest-widget .monaco-icon-name-container,
        .monaco-editor .suggest-widget .monaco-icon-name-container {
            font-weight: 600 !important;
        }

        /* Secondary line (path) - muted, smaller */
        .overflowingContentWidgets .suggest-widget .monaco-icon-description-container,
        .monaco-editor .suggest-widget .monaco-icon-description-container {
            margin-left: 8px !important;
            opacity: 0.7 !important;
            white-space: nowrap !important;
            overflow: hidden !important;
            text-overflow: ellipsis !important;
        }

        /* Dark mode support for suggest widget - high specificity to override Monaco defaults */
        html[data-theme="dark"] .monaco-editor .suggest-widget,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget,
        html[data-theme="dark"] .suggest-widget.monaco-editor-overlaymessage,
        :root[data-theme="dark"] .suggest-widget {
            background-color: #252526 !important;
            border: 1px solid #454545 !important;
            color: #cccccc !important;
        }

        html[data-theme="dark"] .monaco-editor .suggest-widget .monaco-list,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget .monaco-list,
        :root[data-theme="dark"] .suggest-widget .monaco-list {
            background-color: #252526 !important;
        }

        html[data-theme="dark"] .monaco-editor .suggest-widget .monaco-list-row,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget .monaco-list-row,
        :root[data-theme="dark"] .suggest-widget .monaco-list-row {
            color: #cccccc !important;
            background-color: transparent !important;
        }

        html[data-theme="dark"] .monaco-editor .suggest-widget .monaco-list-row.focused,
        html[data-theme="dark"] .monaco-editor .suggest-widget .monaco-list-row.selected,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget .monaco-list-row.focused,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget .monaco-list-row.selected,
        :root[data-theme="dark"] .suggest-widget .monaco-list-row.focused,
        :root[data-theme="dark"] .suggest-widget .monaco-list-row.selected {
            background-color: #094771 !important;
            color: #ffffff !important;
        }

        html[data-theme="dark"] .monaco-editor .suggest-widget .details-label,
        html[data-theme="dark"] .overflowingContentWidgets .suggest-widget .details-label,
        :root[data-theme="dark"] .suggest-widget .details-label {
            color: #8a8a8a !important;
        }

        /* Also apply dark mode via system preference as fallback */
        @media (prefers-color-scheme: dark) {
            .suggest-widget {
                background-color: #252526 !important;
                border: 1px solid #454545 !important;
                color: #cccccc !important;
            }

            .suggest-widget .monaco-list {
                background-color: #252526 !important;
            }

            .suggest-widget .monaco-list-row {
                color: #cccccc !important;
                background-color: transparent !important;
            }

            .suggest-widget .monaco-list-row.focused,
            .suggest-widget .monaco-list-row.selected {
                background-color: #094771 !important;
                color: #ffffff !important;
            }

            .suggest-widget .details-label {
                color: #8a8a8a !important;
            }
        }
    `;
    document.head.appendChild(style);
})();

export function initEditor(editorId, placeholder, dotNetRef, codeEditMode = false, showLineNumbers = false) {
    const container = document.getElementById(editorId);
    if (!container) {
        console.error('Container not found:', editorId);
        return;
    }

    // Register theme sync callback (only once, when first editor initializes)
    ensureThemeCallbackRegistered();

    // Get the monaco editor instance via BlazorMonaco's registry (reliable lookup)
    const editorInstance = window.blazorMonaco?.editor?.getEditor(editorId);

    // Store state for this editor
    editorState.set(editorId, {
        dotNetRef: dotNetRef,
        editorInstance: editorInstance,
        annotationDecorationIds: [],
        completionConfig: null,
        completionDisposable: null,
        codeEditMode: codeEditMode,
        showLineNumbers: showLineNumbers
    });

    // Add placeholder styling
    updatePlaceholder(editorId, placeholder, showLineNumbers);

    // Apply theme immediately to this editor instance to ensure correct colors
    // This is needed because the editor may be created before the global theme sync runs
    if (editorInstance) {
        const currentTheme = detectThemeFromDOM();
        const monacoTheme = currentTheme === 'dark' ? 'vs-dark' : 'vs';
        monaco.editor.setTheme(monacoTheme);
    }
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

            // Allow Alt+Enter for new line in chat mode (same as Shift+Enter)
            editorInstance.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.Enter, () => {
                editorInstance.trigger('keyboard', 'type', { text: '\n' });
            });
        }
        // In code edit mode, Enter naturally inserts newlines (default Monaco behavior)

        // Handle blur event - delay to check if focus moved to autocomplete
        editorInstance.onDidBlurEditorWidget(async () => {
            // Small delay to allow focus to settle (autocomplete popup steals focus)
            await new Promise(resolve => setTimeout(resolve, 100));

            // Check if autocomplete is visible - don't fire blur if it is
            const suggestController = editorInstance.getContribution('editor.contrib.suggestController');
            const suggestState = suggestController?.model?.state;
            const isSuggestVisible = typeof suggestState === 'number' && suggestState > 0;

            if (isSuggestVisible) {
                return; // Don't fire blur while autocomplete is open
            }

            // Also check if editor regained focus
            if (editorInstance.hasTextFocus()) {
                return; // Focus returned to editor
            }

            const currentState = editorState.get(editorId);
            if (currentState?.dotNetRef) {
                try {
                    await currentState.dotNetRef.invokeMethodAsync('HandleBlur');
                } catch (err) {
                    // Ignore errors - component may have been disposed
                }
            }
        });

        // Force layout after initialization
        setTimeout(() => {
            editorInstance.layout();
        }, 100);
    } else {
        console.error('Editor instance not found for', editorId);
    }
}

function updatePlaceholder(editorId, placeholder, showLineNumbers = false) {
    const container = document.getElementById(editorId);
    if (!container) return;

    // Calculate left offset based on line numbers
    // When line numbers are shown, we need to account for the gutter width
    // Monaco uses ~40px for 3-char line numbers + some padding
    const leftOffset = showLineNumbers ? 35 : 10;

    // Create or update placeholder element
    let placeholderEl = container.querySelector('.monaco-placeholder');
    if (!placeholderEl) {
        placeholderEl = document.createElement('div');
        placeholderEl.className = 'monaco-placeholder';
        placeholderEl.style.cssText = `
            position: absolute;
            top: 8px;
            left: ${leftOffset}px;
            color: var(--neutral-foreground-hint, #605e5c);
            pointer-events: none;
            font-size: 14px;
            font-family: var(--body-font, "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif);
            z-index: 1;
        `;
        container.style.position = 'relative';
        container.appendChild(placeholderEl);
    } else {
        // Update left position if element already exists
        placeholderEl.style.left = `${leftOffset}px`;
    }
    placeholderEl.textContent = placeholder;

    // Check initial visibility
    const state = editorState.get(editorId);
    if (state?.editorInstance) {
        const value = state.editorInstance.getValue();
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
    const language = config?.language || 'plaintext';
    let items = [];
    if (Array.isArray(config?.items)) {
        items = config.items;
    } else if (config?.items && typeof config.items === 'object') {
        items = Object.values(config.items);
    }

    state.completionConfig = { triggerCharacters, items, useAsync, language };
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

    // Register a command that fires when a completion item is accepted.
    // keybinding=0 means no keyboard shortcut — invoked only via CompletionItem.command.
    if (!state.completionCommandId && state.editorInstance) {
        state.completionCommandId = state.editorInstance.addCommand(0, (_, path) => {
            const currentState = editorState.get(editorId);
            if (currentState?.dotNetRef && path) {
                currentState.dotNetRef.invokeMethodAsync('HandleCompletionAccepted', path);
            }
        });
    }

    // Build trigger character set for regex (not used directly anymore, but kept for reference)
    const escapedTriggers = triggerCharacters.map(c => c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('');

    // Create debounced async fetch function (50ms delay)
    const debouncedFetch = debounce(async (query) => {
        if (!state.dotNetRef) {
            return [];
        }
        try {
            state.isCompletionPending = true;
            return await state.dotNetRef.invokeMethodAsync('GetAsyncCompletions', query);
        } catch (e) {
            console.error('Error fetching async completions:', e);
            return [];
        } finally {
            state.isCompletionPending = false;
        }
    }, 50);

    // Register new completion provider for the specified language
    // Note: Monaco registers providers globally per language, so we need to check
    // if this request is for our specific editor instance
    state.completionDisposable = monaco.languages.registerCompletionItemProvider(language, {
        triggerCharacters: triggerCharacters,
        provideCompletionItems: async (model, position) => {
            // Check if this model belongs to our editor
            const editorInstance = editorState.get(editorId)?.editorInstance;
            if (!editorInstance || editorInstance.getModel() !== model) {
                // This completion request is not for our editor, skip it
                return null;
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

            // Get the configured trigger characters
            const configuredTriggers = currentState?.completionConfig?.triggerCharacters || ['@'];

            // Check if we're after a configured trigger character
            let triggerMatch = null;

            for (const trigger of configuredTriggers) {
                // Escape the trigger character for regex
                const escapedTrigger = trigger.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

                // @ can be followed by paths with slashes: @agent/Name, @content/path/file
                // / is only a trigger at word boundary (for commands like /agent)
                let regex;
                if (trigger === '/') {
                    regex = new RegExp(`(?:^|\\s)${escapedTrigger}([\\w\\-\\.]+)?$`);
                } else {
                    regex = new RegExp(`${escapedTrigger}([\\w\\-\\./]+)?$`);
                }

                const match = textUntilPosition.match(regex);
                if (match) {
                    if (trigger === '/') {
                        // Adjust match to not include the leading space
                        const fullMatch = match[0];
                        const slashIndex = fullMatch.indexOf('/');
                        match[0] = fullMatch.substring(slashIndex);
                    }
                    triggerMatch = match;
                    break;
                }
            }

            if (!triggerMatch) {
                return { suggestions: [] };
            }

            const triggerChar = triggerMatch[0].charAt(0);
            const afterTrigger = triggerMatch[1] || '';

            // Include trigger char in query for server to determine context
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
                const searchTermLower = afterTrigger.toLowerCase();
                currentItems = allItems.filter(item =>
                    item && item.label &&
                    (item.label.toLowerCase().includes(searchTermLower) ||
                    (item.description && item.description.toLowerCase().includes(searchTermLower)))
                );
            }

            if (!Array.isArray(currentItems)) {
                return { suggestions: [] };
            }

            const suggestions = currentItems.map((item) => {
                // filterText must match what the user typed (fullQuery includes the trigger char)
                // Use insertText as filterText since that's what matches the typed pattern
                const filterText = item.insertText || item.label;

                // Simple single-line display: Path as label, category as detail
                // This avoids row height calculation issues with multi-line labels
                const displayLabel = item.path || item.label;

                const suggestion = {
                    label: displayLabel,
                    kind: typeof item.kind === 'number' ? item.kind : monaco.languages.CompletionItemKind.Text,
                    insertText: item.insertText || item.label,
                    range: range,
                    detail: item.category || '',          // Category shown on the right
                    documentation: item.description ? {   // Full description on hover
                        value: item.description
                    } : undefined,
                    filterText: filterText,
                    sortText: displayLabel.toLowerCase()  // Sort alphabetically by path
                };

                // Attach command to notify C# when a suggestion is accepted
                if (currentState.completionCommandId && item.path) {
                    suggestion.command = {
                        id: currentState.completionCommandId,
                        title: '',
                        arguments: [item.path]
                    };
                }

                return suggestion;
            });

            // Set incomplete: true for async mode to allow re-fetching as user types
            return { suggestions, incomplete: isAsync };
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
    const editorInstance = editorState.get(editorId)?.editorInstance;
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

// Trigger the suggestion/autocomplete popup programmatically
export function triggerSuggest(editorId) {
    const editorInstance = editorState.get(editorId)?.editorInstance;
    if (editorInstance) {
        // Trigger the suggest action
        editorInstance.trigger('keyboard', 'editor.action.triggerSuggest', {});
        return true;
    }
    return false;
}

// Set cursor position to end of content
export function setCursorToEnd(editorId) {
    const editorInstance = editorState.get(editorId)?.editorInstance;
    if (editorInstance) {
        const model = editorInstance.getModel();
        if (model) {
            const lastLine = model.getLineCount();
            const lastColumn = model.getLineMaxColumn(lastLine);
            editorInstance.setPosition({ lineNumber: lastLine, column: lastColumn });
            return true;
        }
    }
    return false;
}

// =============================================================================
// Annotation Decorations for Track Changes
// =============================================================================

// Generate a short unique marker ID
function generateMarkerId() {
    return Date.now().toString(36) + Math.random().toString(36).substring(2, 6);
}

/**
 * Updates Monaco editor decorations to visually highlight annotation ranges.
 * Accepts pre-computed ranges from C# (offsets in clean content).
 * Each range: { type: 'insert'|'delete'|'comment', start: number, end: number }
 */
export function updateAnnotationDecorations(editorId, ranges) {
    const state = editorState.get(editorId);
    if (!state?.editorInstance) return;

    // If no ranges provided, keep existing decorations (no-op)
    if (!Array.isArray(ranges)) return;

    const editorInstance = state.editorInstance;
    const model = editorInstance.getModel();
    if (!model) return;

    const decorations = [];

    for (const range of ranges) {
            const startPos = model.getPositionAt(range.start);
            const endPos = model.getPositionAt(range.end);

            let className;
            switch (range.type) {
                case 'insert':
                    className = 'monaco-insert-decoration';
                    break;
                case 'delete':
                    className = 'monaco-delete-decoration';
                    break;
                case 'comment':
                    className = 'monaco-comment-decoration';
                    break;
                default:
                    continue;
            }

            decorations.push({
                range: new monaco.Range(startPos.lineNumber, startPos.column, endPos.lineNumber, endPos.column),
                options: {
                    inlineClassName: className,
                    stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges
                }
            });
    }

    // Store decoration IDs on state for future delta updates
    state.annotationDecorationIds = editorInstance.deltaDecorations(
        state.annotationDecorationIds || [],
        decorations
    );
}

/**
 * Enables or disables track changes mode for the editor.
 * When enabled, new edits are wrapped in <!--insert:id--> markers
 * and deletions of non-marker text are wrapped in <!--delete:id--> markers.
 */
/**
 * Enables or disables track changes mode.
 * @param {string} editorId - The editor instance ID
 * @param {boolean} enabled - Whether to enable track changes
 * @param {string} [author] - Author name to embed in markers
 */
export function setTrackChangesMode(editorId, enabled, author) {
    const state = editorState.get(editorId);
    if (!state?.editorInstance) return;
    const editorInstance = state.editorInstance;

    state.trackChangesEnabled = enabled;
    state.trackChangesAuthor = author || '';

    if (enabled && !state.trackChangesDisposable) {
        state.trackChangesProcessing = false;

        // Listen to content changes and wrap them in annotation markers
        state.trackChangesDisposable = editorInstance.onDidChangeModelContent((e) => {
            if (state.trackChangesProcessing || !state.trackChangesEnabled) return;

            const model = editorInstance.getModel();
            if (!model) return;

            const authorStr = state.trackChangesAuthor;
            const dateStr = formatShortDate();
            const metaSuffix = authorStr ? `:${authorStr}:${dateStr}` : '';

            // Process each change in the event
            const edits = [];
            let needsUpdate = false;

            // Process changes in reverse order to maintain position accuracy
            const changes = [...e.changes].sort((a, b) => b.rangeOffset - a.rangeOffset);

            for (const change of changes) {
                const isInsertion = change.text.length > 0 && change.rangeLength === 0;
                const isDeletion = change.text.length === 0 && change.rangeLength > 0;
                const isReplacement = change.text.length > 0 && change.rangeLength > 0;

                // Check if we're editing inside an existing annotation marker tag
                const content = model.getValue();
                const beforeChange = content.substring(Math.max(0, change.rangeOffset - 200), change.rangeOffset);
                const isInsideTag = /<!--(?:insert|delete|comment):[^>]*$/.test(beforeChange) ||
                                     /<!--\/(?:insert|delete|comment):[^>]*$/.test(beforeChange);
                if (isInsideTag) continue;

                const markerId = generateMarkerId();

                if (isInsertion) {
                    const insertedText = change.text;
                    if (insertedText.trim().length === 0) continue;

                    const pos = model.getPositionAt(change.rangeOffset);
                    const endPos = model.getPositionAt(change.rangeOffset + insertedText.length);
                    edits.push({
                        range: new monaco.Range(pos.lineNumber, pos.column, endPos.lineNumber, endPos.column),
                        text: `<!--insert:${markerId}${metaSuffix}-->${insertedText}<!--/insert:${markerId}-->`
                    });
                    needsUpdate = true;
                } else if (isDeletion) {
                    const deletedText = content.substring(change.rangeOffset, change.rangeOffset + change.rangeLength);
                    if (/^<!--\/?(?:insert|delete|comment):/.test(deletedText)) continue;
                    if (deletedText.trim().length === 0) continue;

                    const pos = model.getPositionAt(change.rangeOffset);
                    edits.push({
                        range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column),
                        text: `<!--delete:${markerId}${metaSuffix}-->${deletedText}<!--/delete:${markerId}-->`
                    });
                    needsUpdate = true;
                } else if (isReplacement) {
                    const deletedText = content.substring(change.rangeOffset, change.rangeOffset + change.rangeLength);
                    if (/^<!--\/?(?:insert|delete|comment):/.test(deletedText)) continue;

                    const delMarkerId = generateMarkerId();
                    const insMarkerId = generateMarkerId();
                    const pos = model.getPositionAt(change.rangeOffset);
                    const endPos = model.getPositionAt(change.rangeOffset + change.text.length);
                    edits.push({
                        range: new monaco.Range(pos.lineNumber, pos.column, endPos.lineNumber, endPos.column),
                        text: `<!--delete:${delMarkerId}${metaSuffix}-->${deletedText}<!--/delete:${delMarkerId}--><!--insert:${insMarkerId}${metaSuffix}-->${change.text}<!--/insert:${insMarkerId}-->`
                    });
                    needsUpdate = true;
                }
            }

            if (needsUpdate && edits.length > 0) {
                state.trackChangesProcessing = true;
                try {
                    editorInstance.executeEdits('track-changes', edits);
                } finally {
                    state.trackChangesProcessing = false;
                }
            }
        });
    } else if (!enabled && state.trackChangesDisposable) {
        state.trackChangesDisposable.dispose();
        state.trackChangesDisposable = null;
    }
}

/**
 * Registers a "Add Comment" action in the Monaco editor context menu.
 * When triggered, calls the Blazor callback with the selection offset range.
 * @param {string} editorId - The editor instance ID
 * @param {object} dotNetRef - DotNet object reference for callback
 * @param {string} callbackMethod - Name of the [JSInvokable] method to call
 */
export function registerCommentAction(editorId, dotNetRef, callbackMethod) {
    const state = editorState.get(editorId);
    if (!state?.editorInstance) return;
    const editorInstance = state.editorInstance;

    // Enable context menu
    editorInstance.updateOptions({ contextmenu: true });

    state.commentActionDisposable = editorInstance.addAction({
        id: 'add-comment',
        label: 'Add Comment',
        contextMenuGroupId: '9_cutcopypaste',
        contextMenuOrder: 100,
        precondition: 'editorHasSelection',
        run: (ed) => {
            const selection = ed.getSelection();
            if (!selection || selection.isEmpty()) return;
            const model = ed.getModel();
            if (!model) return;
            const startOffset = model.getOffsetAt(selection.getStartPosition());
            const endOffset = model.getOffsetAt(selection.getEndPosition());
            dotNetRef.invokeMethodAsync(callbackMethod, startOffset, endOffset);
        }
    });
}

/**
 * Sets the editor value while suppressing the track changes handler.
 * Use this for programmatic content updates (e.g. accept/reject annotations)
 * to prevent the track changes handler from re-wrapping the change in markers.
 */
export function setValueSuppressTracking(editorId, value) {
    const state = editorState.get(editorId);
    if (!state?.editorInstance) return;

    state.trackChangesProcessing = true;
    try {
        state.editorInstance.setValue(value);
    } finally {
        // Use setTimeout to ensure the synchronous onDidChangeModelContent handler
        // has already fired before we re-enable tracking
        setTimeout(() => {
            if (editorState.has(editorId)) {
                editorState.get(editorId).trackChangesProcessing = false;
            }
        }, 0);
    }
}

function formatShortDate() {
    const months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
    const d = new Date();
    return `${months[d.getMonth()]} ${d.getDate()}`;
}

/**
 * Navigates the editor cursor to a specific annotation marker by ID.
 * Returns the line number if found, or 0 if not found.
 */
export function navigateToAnnotation(editorId, markerId) {
    const state = editorState.get(editorId);
    if (!state?.editorInstance) return 0;
    const editorInstance = state.editorInstance;

    const model = editorInstance.getModel();
    if (!model) return 0;

    const content = model.getValue();
    const markerRegex = new RegExp(`<!--(?:insert|delete|comment):${markerId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}[^>]*-->`, 'g');
    const match = markerRegex.exec(content);
    if (!match) return 0;

    const pos = model.getPositionAt(match.index);
    editorInstance.setPosition(pos);
    editorInstance.revealLineInCenter(pos.lineNumber);
    editorInstance.focus();
    return pos.lineNumber;
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
