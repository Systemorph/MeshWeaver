import { contractMessage } from "@open-smc/application/src/contractMessage";
import { BaseEvent, EventStatus } from "@open-smc/application/src/application.contract";

@contractMessage("OpenSmc.Notebook.OpenProjectExplorerEvent")
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

@contractMessage("OpenSmc.Notebook.FolderAddedEvent")
export class FolderAddedEvent extends ProjectNodeAddedEvent {
    constructor(public name: string, public parentPath: string) {
        super(name, parentPath);
    }
}

@contractMessage("OpenSmc.Notebook.NotebookAddedEvent")
export class NotebookAddedEvent extends ProjectNodeAddedEvent {
    constructor(public name: string, public parentPath: string) {
        super(name, parentPath);
    }
}

@contractMessage("OpenSmc.Notebook.ProjectNodeMoveEvent")
export class ProjectNodeMoveEvent extends ProjectExplorerChangedEvent {
    constructor(public path: string, public newPath: string) {
        super();
    }
}

@contractMessage("OpenSmc.Notebook.DeleteNodeEvent")
export class DeleteNodeEvent extends ProjectExplorerChangedEvent {
    constructor(public path: string) {
        super();
    }
}
