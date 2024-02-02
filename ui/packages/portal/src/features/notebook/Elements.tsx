import { AddElement } from "./AddElement";
import styles from "./notebook.module.scss";
import { useEffect } from "react";
import { useSideMenu } from "../components/sideMenu/hooks/useSideMenu";
import { triggerContentWidthChanged } from "./contentWidthEvent";
import { debounce } from "lodash";
import { usePrefixHashFragment } from "../../shared/hooks/usePrefixHashFragment";
import { useNotebookEditorSelector } from "./NotebookEditor";
import { ControlStarter } from "@open-smc/application/src/ControlStarter";
import { useProject } from "../project/projectStore/hooks/useProject";
import { useEnv } from "../project/projectStore/hooks/useEnv";

export function Elements() {
    const permissions = useNotebookEditorSelector("permissions");
    const elementIds = useNotebookEditorSelector("elementIds");
    const {canEdit} = permissions;
    const {project} = useProject();
    const {envId} = useEnv();
    const notebook = useNotebookEditorSelector("notebook");

    usePrefixHashFragment();

    const renderedElements = elementIds.map(elementId => {
            return (
                <div className={styles.elementsContainer} key={elementId}>
                    <ControlStarter area={`element-${elementId}`} path={`notebookElement/${project.id}/${envId}/${notebook.id}/${elementId}`}/>
                    {canEdit && <AddElement afterElementId={elementId}/>}
                </div>
            );
        }
    );

    return (
        <div className={styles.elementsWrapper} id='popup-container'>
            {canEdit && <AddElement alwaysVisible={renderedElements.length === 0}/>}
            {renderedElements}
        </div>
    );
}