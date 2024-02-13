import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { NotebookEditor } from "@open-smc/sandbox/src/NotebookEditor";
import { v4 } from "uuid";
import { NotebookElementDto } from "@open-smc/portal/src/controls/ElementEditorControl";
import { last } from "lodash";
import { PropsWithChildren, useMemo, useState } from "react";
import { getOrAdd } from "@open-smc/utils/src/getOrAdd";
import { ViewModelHub } from "@open-smc/sandbox/src/ViewModelHub";
import { SetAreaRequest } from "@open-smc/application/src/contract/application.contract";

import myNotebook1 from "./notebooks/myNotebook1.json";
import { ProjectContextProvider } from "@open-smc/portal/src/features/project/projectStore/projectStore";
import { ProjectNode } from "@open-smc/portal/src/app/projectApi";
import { ApiContext, apiContext } from "@open-smc/portal/src/ApiProvider";
import { Permission } from "@open-smc/portal/src/features/accessControl/accessControl.contract";
import { ControlBase } from "@open-smc/sandbox/src/ControlBase";

const elements: NotebookElementDto[] = myNotebook1.cells.map((cell: any) => {
    const {cell_type, source, language} = cell;

    return {
        id: v4(),
        elementKind: cell_type,
        content: source.join(""),
        language,
        evaluationStatus: "Idle",
    } as NotebookElementDto
});

export function NotebookEditorPage() {
    const [layoutHub] = useState(new NotebookEditorLayoutHub(elements));

    const projectState = useMemo(() => {
        const envId = "dev";

        return {
            project: {id: "myProject"},
            currentEnv: {
                envId,
                env: {
                    id: envId
                }
            },
            permissions: {
                canEdit: true,
                isOwner: true
            },
            activeFile: {
                id: "1",
                path: "myNotebook",
                kind: "Notebook",
                name: "myNotebook"
            } as ProjectNode,
        } as any
    }, []);

    return (
        <ApiProviderMock>
            <ProjectContextProvider state={projectState}>
                <Sandbox
                    layoutHub={layoutHub}
                    path={"notebook/1"}
                    log={true}
                />
            </ProjectContextProvider>
        </ApiProviderMock>
    );
}

class NotebookEditorLayoutHub extends ViewModelHub {
    private readonly controlsByArea = new Map<string, ControlBase>();
    private notebookEditor: NotebookEditor;

    constructor(private elements: NotebookElementDto[]) {
        super();

        this.receiveMessage(SetAreaRequest, (event => {
            const {area} = event;
            const control = getOrAdd(this.controlsByArea, area, () => event ? this.controlFactory(event) : null);
            this.setArea(area, control);
        }));
    }

    controlFactory(event: SetAreaRequest): ControlBase {
        const parts = event.path?.split("/");

        if (parts?.[0] === "notebook") {
            return this.notebookEditor = new NotebookEditor(this.elements);
        }

        if (parts?.[0] === "notebookElement") {
            const elementId = last(parts);
            return this.notebookEditor.getElementEditor(elementId);
        }
    }
}

function ApiProviderMock({children}: PropsWithChildren) {
    const value = useMemo(() => {
        return {
            ProjectApi: null,
            ProjectAccessControlApi: null,
            EnvAccessControlApi: {
                async getPermission(projectId: string, permission: Permission, envId: string, objectId?: string) {
                    return true;
                }
            },
            ProjectSessionApi: {
                async getTiers() {
                    return [];
                },

                async getImages() {
                    return [];
                }

            },
            EnvSessionApi: {
                async getSessionSettings(projectId: string, envId: string, objectId?: string) {
                    return {} as any
                }
            }
        } as ApiContext;
    }, []);

    return (
        <apiContext.Provider value={value} children={children}/>
    );
}
