import { useEffect } from "react";
import { debounce, keys } from "lodash";
import { useElementsStore, useNotebookEditorSelector } from "../../NotebookEditor";
import { applyContentEdits } from "../applyContentEdits";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { NotebookElementContentChangedEvent } from "../../notebookElement.contract";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";

export function useSubscribeToElementContentChanged() {
    const notebook = useNotebookEditorSelector("notebook");
    const elementsStore = useElementsStore();
    const updateMarkdown = useUpdateMarkdown();
    const {getState} = useElementsStore();
    const elementIds = useNotebookEditorSelector("elementIds");
    const {receiveMessage} = useMessageHub();

    useEffect(() => {
        let buffer: NotebookElementContentChangedEvent[] = [];

        const applyChangesDebounced = debounce(() => {
            const elements = getState();

            const contentByElementId: Record<string, string> = {};

            buffer.forEach(event => {
                const {elementId, changes} = event;

                if (elementIds.includes(elementId)) {
                    let content = contentByElementId[elementId] ?? elements[elementId].element.content;
                    contentByElementId[elementId] = applyContentEdits(content, changes);

                    const {editorRef} = elements[elementId];

                    if (editorRef.current) {
                        changes.forEach(e => editorRef.current.applyEdits([e]));
                    }
                }
            });

            const changedElementIds = keys(contentByElementId);

            const models = elementsStore.setState(models => {
                for (const elementId of changedElementIds) {
                    models[elementId].element.content = contentByElementId[elementId];
                }
            });

            const markdownElementIds = changedElementIds.map(elementId => models[elementId].element)
                .filter(e => e.elementKind === 'markdown')
                .map(e => e.id);

            if (markdownElementIds.length > 0) {
                updateMarkdown(markdownElementIds);
            }

            buffer = [];
        }, 100);

        return receiveMessage(
            NotebookElementContentChangedEvent,
            event => {
                buffer.push(event);
                applyChangesDebounced();
            },
            ({message}) => message.notebookId === notebook.id
        );
    }, [notebook, receiveMessage, updateMarkdown, getState, elementIds]);
}