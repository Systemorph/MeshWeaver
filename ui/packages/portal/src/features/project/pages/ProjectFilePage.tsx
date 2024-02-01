import { useEffect, useState } from "react";
import { ProjectApi } from "../../../app/projectApi";
import { useProject } from "../projectStore/hooks/useProject";
import MarkdownViewer from "../components/viewers/MarkdownViewer";
import BlobViewer from "../components/viewers/BlobViewer";
import ImageViewer from "../components/viewers/ImageViewer";
import mime from "mime/lite";
import { AxiosError } from "axios";
import { useParams } from "react-router-dom";
import { useEnv } from "../projectStore/hooks/useEnv";
import page from "../../errorpage.module.scss";
import styles from '../../project/components/viewers/blob-viewer.module.scss';
import "./project-overview.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import { ControlStarter } from "@open-smc/application/ControlStarter";
import { useProjectSelector, useProjectStore } from "../projectStore/projectStore";
import { debounce } from "lodash";
import { triggerContentWidthChanged } from "../../notebook/contentWidthEvent";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";

export type ProjectFilePageParams = {
    fileId?: string;
    '*': string; // file path
}

export function ProjectFilePage() {
    const activeFile = useProjectSelector("activeFile");
    const {setState} = useProjectStore();
    const [loading, setLoading] = useState(true);
    const {project} = useProject();
    const {envId} = useEnv();
    const {'*': filePath} = useParams<ProjectFilePageParams>();
    const [fileError, setFileError] = useState<JSX.Element>();
    const {currentMenu} = useSideMenu();

    useEffect(() => {
        const debounced = debounce(triggerContentWidthChanged, 500);
        window.addEventListener('resize', debounced);
        return () => window.removeEventListener('resize', debounced);
    }, []);

    useEffect(() => {
        triggerContentWidthChanged();
    }, [!!currentMenu]);

    useEffect(() => {
        (async () => {
            if (activeFile?.path === filePath) {
                return;
            }
            setFileError(null);
            setLoading(true);
            try {
                const file = await ProjectApi.getNode(project.id, envId, filePath);
                setState(state => {
                    state.activeFile = file;
                });
            } catch (error) {
                setFileError(getFileError(error));
            }

            setLoading(false);
        })();
    }, [project.id, envId, filePath]);

    useEffect(() => void setState(state => state.activeFile = null), []);

    if (loading) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (fileError) {
        return <div className={page.errorContainer}>{fileError}</div>;
    }

    const getRenderedContent = () => {
        switch (activeFile.kind) {
            case 'Notebook':
                const notebookId = activeFile.id;
                return <ControlStarter area={`notebook-${notebookId}`} path={`notebook/${project.id}/${envId}/${notebookId}`}/>
            case 'Blob':
                const mimeType = (mime as any).getType(activeFile.name);

                if (/^image\/[^\/]+$/.test(mimeType)) {
                    return <ImageViewer file={activeFile}/>;
                }

                switch (mimeType) {
                    case "text/markdown":
                        return <MarkdownViewer file={activeFile}/>;
                    default:
                        return <BlobViewer file={activeFile}/>;
                }
            default:
                return <div>Unsupported file type</div>;
        }
    }

    return (
        <div className={`rendered-content-container ${activeFile.kind === 'Blob' ? styles.blob : ''}`}
             data-qa-file-kind={activeFile.kind} data-qa-file-name={activeFile.name}>
            {getRenderedContent()}
        </div>
    );
}

function getFileError(error: unknown) {
    if ((error as AxiosError).response) {
        const status = (error as AxiosError)?.response?.status;

        switch (status) {
            case 404:
                return (
                    <div className={page.fileNotFound}>
                        <p title={`${status}: File not found`} className={page.description}>Sorry, we tried but could
                            not locate the file you requested.</p>
                    </div>
                );
            case 403:
                return (
                    <div className={page.fileAccessDenied}>
                        <p title={`${status}: Access denied`} className={page.description}>Bummer, it looks like you do
                            not have access to this awesome file yet.</p>
                    </div>
                );
            default:
                break;
        }
    }

    return (
        <div className={page.fileDefaultError}>
            <p className={page.description}>Mercury must be in Retrograde, because we failed to load the file.</p>
        </div>
    );
}