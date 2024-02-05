import { contractMessage } from "@open-smc/application/src/contractMessage";
import { NotebookChangedEvent } from "./notebookEditor/notebookEditor.contract";
import { EvaluationStatus } from "../../app/notebookFormat";

export class NotebookElementChangedEvent extends NotebookChangedEvent {
    constructor(public elementId: string) {
        super();
    }
}

@contractMessage("OpenSmc.Notebook.NotebookElementContentChangedEvent")
export class NotebookElementContentChangedEvent extends NotebookElementChangedEvent {
    constructor(public elementId: string,
                public changes: NotebookElementChangeData[]) {
        super(elementId);
    }
}

export interface NotebookElementChangeData {
    readonly startLineNumber: number;
    readonly startColumn: number;
    readonly endLineNumber: number;
    readonly endColumn: number;
    readonly text: string;
}

@contractMessage("OpenSmc.Notebook.NotebookElementEvaluationStatusEvent")
export class NotebookElementEvaluationStatusEvent {
    readonly elementId: string;
    readonly evaluationStatus: EvaluationStatus;
    readonly evaluationCount: number;
    readonly evaluationTime: number;
    readonly evaluationError: string;
}