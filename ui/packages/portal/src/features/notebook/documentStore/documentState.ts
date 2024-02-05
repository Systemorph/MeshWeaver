import {
    ElementKind,
    EvaluationStatus,
    SessionDescriptor
} from "../../../app/notebookFormat";
import { ParsedMarkdown } from "../markdownParser";
import { DebouncedFunc } from "lodash";
import { createRef, RefObject } from "react";
import { ElementEditorApi, FocusMode } from "../ElementEditor";
import { NotebookDto } from "../../../controls/NotebookEditorControl";
import { NotebookElementDto, ElementEditorView } from "../../../controls/ElementEditorControl";
import { NotebookElementChangeData } from "../notebookElement.contract";
import { AreaChangedEvent } from "@open-smc/application/application.contract";
import { ControlDef } from "@open-smc/application/ControlDef";

export const CODE_COLLAPSED = 'codeCollapsed';
export const SHOW_OUTLINE = 'showOutline';

export interface NotebookEditorState  {
    readonly permissions: NotebookPermissions,
    readonly notebook: NotebookDto;
    readonly elementIds: string[];
    readonly projectId: string;
    readonly envId: string;
    readonly markdown?: Record<string, ElementMarkdown>;
    readonly updateMarkdownDebouncedFunc: DebouncedFunc<(func: () => void) => void>;
    readonly activeElementId?: string;
    readonly focusMode?: FocusMode;
    readonly selectedElementIds: string[];
    readonly clipboard?: Clipboard;
    readonly evaluationStatus?: EvaluationStatus;
    readonly showOutline?: boolean;
    readonly codeCollapsed?: boolean;
    readonly elementChanges: ElementChanges;
    readonly elementEvents?: boolean;
    readonly sessionDialog?: ControlDef;
    readonly session?: SessionDescriptorModel;
}

export type SessionDescriptorModel = Pick<SessionDescriptor, 'status'>;

export type NotebookPermissions = {
    canEdit: boolean;
    canRun: boolean;
}

export type NotebookElementModel = {
    readonly elementId: string;
    readonly ref: RefObject<HTMLDivElement>;
    readonly editorRef: RefObject<ElementEditorApi>;
    readonly isEditMode?: boolean;
    readonly isAdded?: boolean;
    readonly element?: NotebookElementDto;
    readonly output?: AreaChangedEvent;
}

export type ElementMarkdown = {
    readonly parsedMarkdown: ParsedMarkdown;
    readonly html: string;
}

export type Clipboard = {
    readonly elementIds: string[];
    readonly operation: "copy" | "cut";
}

export type ElementChanges = {
    readonly elementId?: string;
    readonly buffer: NotebookElementChangeData[];
    readonly debouncedFunc: DebouncedFunc<(func: () => void) => void>;
}

// export function getNewElementModel(id: string,
//                                    elementKind: ElementKind,
//                                    content: string,
//                                    isEditMode: boolean) {
//     const element = {
//         id,
//         elementKind,
//         evaluationStatus: elementKind === 'code' ? 'Idle' : null,
//         content: content ?? "",
//     };
//
//     return {
//         element,
//         ref: createRef(),
//         editorRef: createRef(),
//         isEditMode,
//         isAdded: true,
//     } as NotebookElementModel;
// }

// export function getElementModel(control: ControlDef<NotebookElementProps>) {
//     const {element} = control;
//
//     return {
//         ref: createRef(),
//         editorRef: createRef(),
//         element,
//         control: {
//             ...control,
//             // TODO: leaving id to obtain element from store (8/10/2023, akravets)
//             element: {id: element.id} as any
//         }
//     } as NotebookElementModel;
// }