import { BaseEvent, EventStatus } from "@open-smc/application/src/contract/application.contract";
import {
    ElementKind,
    EvaluationStatus, SessionDescriptor,
    SessionSpecification
} from "../../../app/notebookFormat";
import { contractMessage } from "@open-smc/application/src/contractMessage";

@contractMessage("MeshWeaver.Notebook.OpenNotebookEvent")
export class OpenNotebookEvent extends BaseEvent {
    constructor(public projectId: string,
                public environment: string,
                public notebookId: string) {
        super();
    }
}

export class NotebookChangedEvent extends BaseEvent {
    notebookId: string;
    status: EventStatus = 'Requested';
}

@contractMessage("MeshWeaver.Notebook.NotebookElementCreatedEvent")
export class NotebookElementCreatedEvent extends NotebookChangedEvent {
    constructor(
        public elementKind: ElementKind,
        public content: string,
        public afterElementId: string,
        public elementId: string) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.NotebookElementMovedEvent")
export class NotebookElementMovedEvent extends NotebookChangedEvent {
    constructor(public readonly elementIds: string[],
                public readonly afterElementId: string) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.NotebookElementDeletedEvent")
export class NotebookElementDeletedEvent extends NotebookChangedEvent {
    constructor(public elementIds: string[]) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.StartSessionEvent")
export class StartSessionEvent extends BaseEvent {
    constructor(public specification: SessionSpecification) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.StopSessionEvent")
export class StopSessionEvent extends BaseEvent {
    constructor() {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.SessionStatusEvent")
export class SessionStatusEvent extends BaseEvent {
    constructor(public session: SessionDescriptor, public errorMessage?: string) {
        super();
    }
}

// @contractMessage("MeshWeaver.Notebook.NotebookElementEvaluationStatusBulkEvent")
// export class NotebookElementEvaluationStatusBulkEvent {
//     readonly elementIds: string[];
//     readonly evaluationStatus: EvaluationStatus;
// }

@contractMessage("MeshWeaver.Notebook.NotebookElementEvaluationStatusEvent")
export class NotebookElementEvaluationStatusEvent {
    readonly elementId: string;
    readonly evaluationStatus: EvaluationStatus;
    readonly evaluationCount: number;
    readonly evaluationTime: number;
    readonly evaluationError: string;

    constructor(elementId: string, evaluationStatus: EvaluationStatus, evaluationCount: number, evaluationTime: number, evaluationError: string) {
        this.elementId = elementId;
        this.evaluationStatus = evaluationStatus;
        this.evaluationCount = evaluationCount;
        this.evaluationTime = evaluationTime;
        this.evaluationError = evaluationError;
    }
}

// @contractMessage("MeshWeaver.Notebook.NotebookElementsClearOutputEvent")
// export class NotebookElementsClearOutputEvent {
//     readonly elementIds: string[];
// }

// @contractMessage("MeshWeaver.Notebook.NotebookElementOutputAddedEvent")
// export class NotebookElementOutputAddedEvent {
//     readonly elementId: string;
//     readonly outputToken: string;
// }

@contractMessage("MeshWeaver.Notebook.EvaluateElementsCommand")
export class EvaluateElementsCommand extends BaseEvent {
    constructor(public elementIds: string[]) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.CancelCommand")
export class CancelCommand {
}

@contractMessage("MeshWeaver.Notebook.SessionEvaluationStatusChangedEvent")
export class SessionEvaluationStatusChangedEvent {
    constructor(public evaluationStatus: EvaluationStatus) {
    }
}

@contractMessage("MeshWeaver.Notebook.DisposeSessionDialogEvent")
export class DisposeSessionDialogEvent {
}

@contractMessage("MeshWeaver.Notebook.ShowSessionDialogEvent")
export class ShowSessionDialogEvent {
    // presenter: PresenterSpec;
}

export type SessionStatus = 'Stopped' | 'Starting' | 'Initializing' | 'Running' | 'Stopping';