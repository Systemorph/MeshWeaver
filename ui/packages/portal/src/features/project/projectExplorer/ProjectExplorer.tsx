import { useProject } from "../projectStore/hooks/useProject";
import { useEnv } from "../projectStore/hooks/useEnv";
import { useEffect, useState } from "react";
import {
    CURRENT_FOLDER,
    CurrentFolderSavedData, FileModel, getFileModel
} from "./projectExplorerStore/fileExplorerState";
import { SessionStorageWrapper } from "../../../shared/utils/sessionStorageWrapper";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";
import { useAddNewFile } from "./projectExplorerStore/hooks/useAddNewFile";
import classNames from "classnames";
import styles from "./project-explorer.module.scss";
import { Button } from "@open-smc/ui-kit/components";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss";
import { Breadcrumbs } from "./Breadcrumbs";
import { FileList } from "./FileList";
import { UploadFormDialog } from "../uploadForm/UploadFormDialog";
import {
    ProjectExplorerContextProvider,
    useFileExplorerSelector,
} from "./ProjectExplorerContextProvider";
import { ProjectExplorerHeader } from "./ProjectExplorerHeader";
import { useSideMenuPanelSelector } from "../../components/sideMenu/SideMenuPanel";
import { ProjectApi } from "../../../app/projectApi";
import BlockUi from "@availity/block-ui";

export function ProjectExplorer() {
    const {project} = useProject();
    const {envId} = useEnv();
    const [loading, setLoading] = useState<boolean>(true);
    const [currentEnv, setCurrentEnv] = useState<string>();
    const [files, setFiles] = useState<FileModel[]>();
    const [path, setPath] = useState<string>();
    const [error, setError] = useState<unknown>();
    const isOpen = useSideMenuPanelSelector("isOpen");

    useEffect(() => {
        (async function () {
            if (isOpen && envId !== currentEnv) {
                setLoading(true);

                try {
                    const savedCurrentFolder = SessionStorageWrapper.getItem(CURRENT_FOLDER) as CurrentFolderSavedData;

                    const path = savedCurrentFolder && savedCurrentFolder.projectId === project.id && savedCurrentFolder.envId === envId
                        ? savedCurrentFolder.path : null;

                    if (!path) {
                        SessionStorageWrapper.setItem(CURRENT_FOLDER, null);
                    }

                    const files = await ProjectApi.getProjectFiles(project.id, envId, path);

                    setFiles(files.map(getFileModel));
                    setPath(path);
                    setCurrentEnv(envId);
                } catch (error) {
                    setError(error);
                }

                setLoading(false);
            }
        })();
    }, [isOpen, envId]);

    if (loading) {
        return <div className={loader.loading}>Loading...</div>
    }

    if (error) {
        return <div>Failed to load explorer</div>;
    }

    return (
        <ProjectExplorerContextProvider
            files={files}
            path={path}
            children={<ProjectExplorerInner/>}
        />
    );
}

function ProjectExplorerInner() {
    const loading = useFileExplorerSelector("loading");
    const addNewFile = useAddNewFile();
    const canEdit = useFileExplorerSelector("canEdit");

    return (
        <div data-qa-explorer>
            <BlockUi blocking={loading} loader={null}>
                <div>
                    <ProjectExplorerHeader/>
                </div>
                <Breadcrumbs/>
                <div className={styles.container}>
                    <FileList/>
                    {canEdit &&
                        <div className={styles.buttonContainer}>
                            <Button
                                className={classNames(button.button, button.secondaryButton, styles.buttonExpand)}
                                onClick={() => addNewFile('Notebook')}
                                icon="sm-overview sm"
                                label="Create Notebook"/>
                        </div>
                    }
                </div>
                <UploadFormDialog/>
            </BlockUi>
        </div>
    );
}