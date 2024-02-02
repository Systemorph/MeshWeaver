import { useSelectElement } from "./useSelectElement";
import { v4 as uuid } from "uuid";
import { getElementModel, useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { ElementKind } from "../../../../app/notebookFormat";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { useMessageHub } from "@open-smc/application/src/messageHub/AddHub";
import { NotebookElementCreatedEvent } from "../../notebookEditor/notebookEditor.contract";
import { insertAfter } from "@open-smc/utils/src/insertAfter";
import { castDraft } from "@open-smc/store/src/store";

export function useCreateElement() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const selectElement = useSelectElement();
    const {sendMessage, receiveMessage} = useMessageHub();
    // const updateMarkdown = useUpdateMarkdown();

    return async (elementKind: ElementKind, afterElementId: string) => {
        const elementId = uuid();

        const event = new NotebookElementCreatedEvent(elementKind, "", afterElementId, elementId);

        const unsubscribe = receiveMessage(
            NotebookElementCreatedEvent,
            event => {
                elementsStore.setState(elements => {
                    elements[elementId] = castDraft(getElementModel(elementId, null, true, true));
                });

                notebookEditorStore.setState(state => {
                    state.elementIds = insertAfter(state.elementIds, elementId, afterElementId);
                });

                // if (elementKind === 'markdown') {
                //     updateMarkdown();
                // }

                selectElement(elementId, false, false, true, 'FirstLine');

                unsubscribe();
            },
            ({message: {eventId, status}}) => eventId === event.eventId && status === "Committed"
        );

        sendMessage(event);
    }
}