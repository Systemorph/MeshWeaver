import { useEffect } from "react";
import { debounce } from "lodash";
import { NotebookElementCreatedEvent } from "../../notebookEditor/notebookEditor.contract";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { castDraft } from "@open-smc/store/store";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import {
    getElementModel,
    useElementsStore,
    useNotebookEditorSelector,
    useNotebookEditorStore
} from "../../NotebookEditor";
import { ElementKind } from "../../../../app/notebookFormat";
import { NotebookElementDto } from "../../../../controls/ElementEditorControl";

export function useSubscribeToNewElements() {
    const notebook = useNotebookEditorSelector("notebook");
    const {receiveMessage} = useMessageHub();
    const flush = useFlushBuffer();

    useEffect(() => {
        let buffer: NotebookElementCreatedEvent[] = [];

        const addNewElementsDebounced = debounce(() => {
            flush(buffer);
            buffer = [];
        }, 100);

        return receiveMessage(
            NotebookElementCreatedEvent,
            event => {
                buffer.push(event);
                addNewElementsDebounced();
            },
            ({message}) => message.notebookId === notebook.id
        );
    }, [receiveMessage, notebook, flush]);
}

function useFlushBuffer() {
    const notebookEditorStore = useNotebookEditorStore();
    // const elementsStore = useElementsStore();
    // const updateMarkdown = useUpdateMarkdown();

    return (events: NotebookElementCreatedEvent[]) => {
        // elementsStore.setState(elements => {
        //     for (const event of events) {
        //         const {elementId, elementKind, content} = event;
        //         const element = getNewElementDto(elementId, elementKind, content);
        //         elements[elementId] = castDraft(getElementModel(elementId, element, true, false));
        //     }
        // });

        notebookEditorStore.setState(({elementIds}) => {
            for (const event of events) {
                const {elementId, afterElementId} = event;
                const index = afterElementId ? elementIds.indexOf(afterElementId) + 1 : 0;
                elementIds.splice(index, 0, elementId);
            }
        });

        // if (events.some(({elementKind}) => elementKind === 'markdown')) {
        //     updateMarkdown();
        // }
    }
}

// function getNewElementDto(id: string,
//                           elementKind: ElementKind,
//                           content: string) {
//     return {
//         id,
//         elementKind,
//         evaluationStatus: elementKind === 'code' ? 'Idle' : null,
//         content: content ?? "",
//     } as NotebookElementDto;
// }