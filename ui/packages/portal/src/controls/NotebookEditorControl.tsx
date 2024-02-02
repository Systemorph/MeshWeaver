import { NotebookEditor } from "../features/notebook/NotebookEditor";
import "../features/notebook/monaco-editor.scss";
import { EvaluationStatus, SessionDescriptor } from "../app/notebookFormat";
import { ControlDef, ControlView } from "@open-smc/application/ControlDef";

export interface NotebookEditorView extends ControlView{
    notebook: NotebookDto;
}

export interface NotebookDto {
    readonly id: string;
    // readonly name: string;
    // readonly createdOn: string;
    // readonly lastModified: string;
    // readonly metadata?: any;
    // readonly version: number;
    readonly currentSession?: SessionDescriptor;
    readonly evaluationStatus?: EvaluationStatus;
    readonly sessionDialog?: ControlDef;
    readonly elementIds: string[];
    readonly projectId: string;
}

export default function NotebookEditorControl({notebook}: NotebookEditorView) {
    return (
        <NotebookEditor
            notebook={notebook}
            projectId={'1'}
            canEdit={true}
            canRun={true}
        />
    );
}

