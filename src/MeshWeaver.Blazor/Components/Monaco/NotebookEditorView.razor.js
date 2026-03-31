// NotebookEditorView.razor.js
// JavaScript module for notebook editor keyboard handling and utilities

const notebookState = new Map();

/**
 * Initialize notebook editor with keyboard handling
 * @param {string} editorId - The unique ID of the notebook editor
 * @param {object} dotNetRef - Reference to the Blazor component
 */
export function initNotebook(editorId, dotNetRef) {
    const state = {
        dotNetRef,
        isInEditMode: false,
        deleteKeyPressed: false,
        deleteKeyTimeout: null
    };

    notebookState.set(editorId, state);

    return {
        dispose: () => dispose(editorId)
    };
}

/**
 * Track if user is in edit mode (typing in Monaco editor)
 * @param {string} editorId - The notebook editor ID
 * @param {boolean} isEditing - Whether user is currently editing
 */
export function setEditMode(editorId, isEditing) {
    const state = notebookState.get(editorId);
    if (state) {
        state.isInEditMode = isEditing;
    }
}

/**
 * Handle double-D delete shortcut
 * @param {string} editorId - The notebook editor ID
 * @returns {boolean} - Whether the delete action should be triggered
 */
export function handleDeleteKey(editorId) {
    const state = notebookState.get(editorId);
    if (!state || state.isInEditMode) {
        return false;
    }

    if (state.deleteKeyPressed) {
        // Second D pressed within timeout - trigger delete
        clearTimeout(state.deleteKeyTimeout);
        state.deleteKeyPressed = false;
        return true;
    }

    // First D pressed - start timeout
    state.deleteKeyPressed = true;
    state.deleteKeyTimeout = setTimeout(() => {
        state.deleteKeyPressed = false;
    }, 500); // 500ms window for double-D

    return false;
}

/**
 * Focus a specific cell's editor
 * @param {string} cellId - The ID of the cell to focus
 */
export function focusCell(cellId) {
    const cellElement = document.querySelector(`[data-cell-id="${cellId}"]`);
    if (cellElement) {
        const editor = cellElement.querySelector('.monaco-editor');
        if (editor) {
            editor.focus();
        } else {
            cellElement.focus();
        }
    }
}

/**
 * Scroll a cell into view
 * @param {string} cellId - The ID of the cell to scroll to
 */
export function scrollToCell(cellId) {
    const cellElement = document.querySelector(`[data-cell-id="${cellId}"]`);
    if (cellElement) {
        cellElement.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
}

/**
 * Clean up notebook state
 * @param {string} editorId - The notebook editor ID
 */
export function dispose(editorId) {
    const state = notebookState.get(editorId);
    if (state) {
        if (state.deleteKeyTimeout) {
            clearTimeout(state.deleteKeyTimeout);
        }
        notebookState.delete(editorId);
    }
}

/**
 * Copy cell content to clipboard
 * @param {string} content - The content to copy
 */
export async function copyToClipboard(content) {
    try {
        await navigator.clipboard.writeText(content);
        return true;
    } catch (err) {
        console.error('Failed to copy to clipboard:', err);
        return false;
    }
}

/**
 * Paste from clipboard
 * @returns {string} - The clipboard content
 */
export async function pasteFromClipboard() {
    try {
        return await navigator.clipboard.readText();
    } catch (err) {
        console.error('Failed to paste from clipboard:', err);
        return '';
    }
}
