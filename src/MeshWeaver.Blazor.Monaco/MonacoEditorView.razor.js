// Chat Monaco Editor JavaScript module
const editorState = new Map();

export function initChatEditor(editorId, placeholder, dotNetRef) {
    console.log('initChatEditor called for', editorId);

    const container = document.getElementById(editorId);
    if (!container) {
        console.error('Container not found:', editorId);
        return;
    }

    // Store state for this editor
    editorState.set(editorId, {
        dotNetRef: dotNetRef,
        agents: [],
        completionDisposable: null
    });

    // Add placeholder styling
    updatePlaceholder(editorId, placeholder);

    // Get the monaco editor instance
    const editorInstance = monaco.editor.getEditors().find(e => e.getContainerDomNode()?.id === editorId);
    if (editorInstance) {
        console.log('Editor instance found for', editorId);

        // Handle content changes for placeholder
        editorInstance.onDidChangeModelContent(() => {
            const value = editorInstance.getValue();
            updatePlaceholderVisibility(editorId, !value);
        });

        // Handle Enter key for submit
        editorInstance.addCommand(monaco.KeyCode.Enter, async () => {
            const state = editorState.get(editorId);
            // Check if suggestion widget is visible
            const suggestWidget = editorInstance._contentWidgets?.['editor.widget.suggestWidget'];
            const isVisible = suggestWidget?.widget?.isVisible?.() || false;

            if (!isVisible) {
                // Submit the message
                if (state?.dotNetRef) {
                    await state.dotNetRef.invokeMethodAsync('HandleSubmit');
                }
            } else {
                // Accept the suggestion
                editorInstance.trigger('keyboard', 'acceptSelectedSuggestion', {});
            }
        }, '!suggestWidgetVisible');

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

export function registerAgentCompletionProvider(editorId, agents) {
    console.log('registerAgentCompletionProvider called for', editorId, 'with agents:', agents);

    const state = editorState.get(editorId);
    if (!state) {
        console.error('No state found for editor:', editorId);
        return;
    }

    // Ensure agents is an array
    let agentList = [];
    if (Array.isArray(agents)) {
        agentList = agents;
    } else if (agents && typeof agents === 'object') {
        agentList = Object.values(agents);
    }

    state.agents = agentList;
    console.log('Agents set to:', state.agents);

    // Dispose previous provider if exists
    if (state.completionDisposable) {
        state.completionDisposable.dispose();
        state.completionDisposable = null;
    }

    // Only register if we have agents
    if (agentList.length === 0) {
        console.log('No agents to register');
        return;
    }

    // Register new completion provider
    state.completionDisposable = monaco.languages.registerCompletionItemProvider('plaintext', {
        triggerCharacters: ['@'],
        provideCompletionItems: (model, position) => {
            const currentState = editorState.get(editorId);
            const currentAgents = currentState?.agents || [];

            if (!Array.isArray(currentAgents)) {
                console.warn('agents is not an array:', currentAgents);
                return { suggestions: [] };
            }

            const textUntilPosition = model.getValueInRange({
                startLineNumber: position.lineNumber,
                startColumn: 1,
                endLineNumber: position.lineNumber,
                endColumn: position.column
            });

            // Check if we're after an @ symbol
            const atMatch = textUntilPosition.match(/@(\w*)$/);
            if (!atMatch) {
                return { suggestions: [] };
            }

            const searchTerm = atMatch[1].toLowerCase();

            // Calculate the range to replace (including the @)
            const atPosition = position.column - atMatch[0].length;
            const range = {
                startLineNumber: position.lineNumber,
                endLineNumber: position.lineNumber,
                startColumn: atPosition,
                endColumn: position.column
            };

            // Filter agents based on search term
            const filteredAgents = currentAgents.filter(agent =>
                agent && agent.name && agent.description &&
                (agent.name.toLowerCase().includes(searchTerm) ||
                agent.description.toLowerCase().includes(searchTerm))
            );

            const suggestions = filteredAgents.map((agent, index) => ({
                label: {
                    label: `@${agent.name}`,
                    description: agent.description
                },
                kind: monaco.languages.CompletionItemKind.User,
                insertText: `@${agent.name} `,
                range: range,
                sortText: String(index).padStart(5, '0'),
                detail: agent.description,
                documentation: agent.description
            }));

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
