import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
// import { getNewElementModel } from "../documentState";
// import { v4 as uuid } from "uuid";
// import { castDraft } from "@open-smc/store/store";
// import { forEachRight, map } from "lodash";
// import { NotebookElementCreatedEvent } from "../../notebookEditor/notebookEditor.contract";
// import { useUpdateMarkdown } from "./useUpdateMarkdown";
// import { useMessageHub } from "@open-smc/application/messageHub/MessageHub";
// import { NotebookElementDto } from "../../NotebookElement";
//
// export function usePasteElements() {
//     const notebookEditorStore = useNotebookEditorStore();
//     const elementsStore = useElementsStore();
//     const updateMarkdown = useUpdateMarkdown();
//     const {sendMessage} = useMessageHub();
//
//     // sends create events in reverse order
//     function sendCreateEvents(elements: NotebookElementDto[], afterElementId: string) {
//         const promises: Promise<NotebookElementCreatedEvent>[] = [];
//
//         forEachRight(elements, ({id, elementKind, content}) => {
//             // TODO: also send language (7/19/2023, akravets)
//             promises.unshift(sendMessage(new NotebookElementCreatedEvent(elementKind, content, afterElementId, id)));
//         });
//
//         return promises;
//     }
//
//     return async() => {
//         const {clipboard, selectedElementIds, elementIds} = notebookEditorStore.getState();
//         const elements = elementsStore.getState();
//         const pasteIndex = getPasteIndex(selectedElementIds, elementIds);
//
//         if (clipboard.operation === "cut") {
//             const cutElements = map(clipboard.elementIds, elementId => elements[elementId].element);
//
//             notebookEditorStore.setState(({clipboard, selectedElementIds, elementIds}) => {
//                 elementIds.splice(pasteIndex, 0, ...clipboard.elementIds);
//                 clipboard.elementIds = null;
//                 clipboard.operation = null;
//             });
//
//             if (cutElements.some(({elementKind}) => elementKind === 'markdown')) {
//                 updateMarkdown();
//             }
//
//             sendCreateEvents(cutElements, elementIds[pasteIndex]);
//         }
//         else {
//             const copies = map(clipboard.elementIds, elementId => {
//                 const {elementKind, content} = elements[elementId].element;
//                 return getNewElementModel(uuid(), elementKind, content, false);
//             });
//
//             elementsStore.setState(models => {
//                 for (const model of copies) {
//                     models[model.element.id] = castDraft(model);
//                 }
//             });
//
//             notebookEditorStore.setState(({clipboard, selectedElementIds, elementIds}) => {
//                 const pasteIndex = getPasteIndex(selectedElementIds, elementIds);
//                 elementIds.splice(pasteIndex, 0, ...copies.map(model => model.element.id));
//                 clipboard.elementIds = null;
//                 clipboard.operation = null;
//             });
//
//             if (copies.some(({element: {elementKind}}) => elementKind === 'markdown')) {
//                 updateMarkdown();
//             }
//
//             sendCreateEvents(copies.map(model => model.element), elementIds[pasteIndex]);
//         }
//     }
// }
//
//
// function getPasteIndex(
//     selectedElementIds: string[],
//     elementIds: string[]
// ): number {
//     let maxSelectedElementIndex = -1;
//     selectedElementIds.forEach((elementId) => {
//         maxSelectedElementIndex =
//             elementIds.indexOf(elementId) > maxSelectedElementIndex
//                 ? elementIds.indexOf(elementId)
//                 : maxSelectedElementIndex;
//     });
//     return maxSelectedElementIndex;
// }