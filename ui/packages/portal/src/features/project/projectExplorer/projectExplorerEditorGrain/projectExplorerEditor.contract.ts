import { contractMessage } from "@open-smc/application/src/contractMessage";
import { BaseEvent, EventStatus } from "@open-smc/application/src/contract/application.contract";

@contractMessage("MeshWeaver.Notebook.OpenProjectExplorerEvent")
export class OpenProjectExplorerEvent extends BaseEvent {
    constructor(public projectId: string, public environment: string) {
        super();
    }
}

class ProjectExplorerChangedEvent {
    public status: EventStatus = 'Requested';
}

class ProjectNodeAddedEvent extends ProjectExplorerChangedEvent{
    public id: string;

    constructor(public name: string, public parentPath: string) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.FolderAddedEvent")
export class FolderAddedEvent extends ProjectNodeAddedEvent {
    constructor(public name: string, public parentPath: string) {
        super(name, parentPath);
    }
}

@contractMessage("MeshWeaver.Notebook.NotebookAddedEvent")
export class NotebookAddedEvent extends ProjectNodeAddedEvent {
    constructor(public name: string, public parentPath: string) {
        super(name, parentPath);
    }
}

@contractMessage("MeshWeaver.Notebook.ProjectNodeMoveEvent")
export class ProjectNodeMoveEvent extends ProjectExplorerChangedEvent {
    constructor(public path: string, public newPath: string) {
        super();
    }
}

@contractMessage("MeshWeaver.Notebook.DeleteNodeEvent")
export class DeleteNodeEvent extends ProjectExplorerChangedEvent {
    constructor(public path: string) {
        super();
    }
}
