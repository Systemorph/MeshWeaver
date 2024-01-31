import { FileModel } from "../../projectExplorer/projectExplorerStore/fileExplorerState";
import { useEnvironmentId } from "../../projectStore/hooks/useEnvironmentId";
import { useProject } from "../../projectStore/hooks/useProject";
import styles from './blob-viewer.module.scss';
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/components/buttons.module.scss"

interface FileProps {
    file: FileModel;
}

export default function BlobViewer({file}: FileProps) {
    const {project} = useProject();
    const environmentId = useEnvironmentId();

    const fileDownloadLink = `/api/project/${project.id}/env/${environmentId}/file/download?path=${file.path}`;
    return (
        <div className={styles.fileDownloadWrapper}>
            <img src={'/download-file.svg'} alt="download file icon" width="134" height="163"/>
            <div className={styles.fileName}>{file.name}</div>
            <a
                href={fileDownloadLink}
                className={classNames(buttons.primaryButton, buttons.buttonLarge, styles.link)}
                children="download"
            />
        </div>
    );
}
