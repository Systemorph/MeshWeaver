import { ProjectNode, ProjectNodeKind } from "../../../../app/projectApi";

export type FileExplorerState = {
    readonly path?: string;
    readonly fileIds?: string[];
    readonly loading?: boolean;
    readonly uploadFormVisible?: boolean;
    readonly keepUploadFormOpen?: boolean;
    readonly canEdit?: boolean;
}

export type FileModel = Pick<ProjectNode, 'name' | 'kind'> & {
    readonly id: string; // model id
    readonly path: string;
    readonly editMode?: boolean;
    readonly actualFileId?: string; // original node id to be used with backend API
}

export function compareFiles(a: FileModel, b: FileModel) {
    const byKind = compareKinds(a.kind, b.kind);

    if (byKind === 0) {
        return a.name.localeCompare(b.name);
    }

    return byKind
}

function compareKinds(a: ProjectNodeKind, b: ProjectNodeKind) {
    return kindAsNum(a) - kindAsNum(b);
}

const kindAsNum = (kind: ProjectNodeKind) => kind === "Folder" ? 0 : 1;

export const CURRENT_FOLDER = 'currentFolder';

export type CurrentFolderSavedData = {
    readonly projectId: string;
    readonly envId: string;
    readonly path?: string;
}

export const getFileModel = (file: ProjectNode) => ({
    id: file.id,
    name: file.name,
    path: file.path,
    kind: file.kind,
    actualFileId: file.id
}) as FileModel;