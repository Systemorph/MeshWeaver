import { FileModel } from "../../projectExplorer/projectExplorerStore/fileExplorerState";
import { useEnvironmentId } from "../../projectStore/hooks/useEnvironmentId";
import { useProject } from "../../projectStore/hooks/useProject";
import styles from './image-viewer.module.scss';
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/src/components/buttons.module.scss"

interface FileProps {
    file: FileModel;
}

export default function ImageViewer({file}: FileProps) {
    const {project} = useProject();
    const environmentId = useEnvironmentId();

    const fileDownloadLink = `/api/project/${project.id}/env/${environmentId}/file/download?path=${file.path}`;
    return (
        <div className={styles.imageViewer}>
            <div className={styles.wrapper}>
                <img className={styles.image} src={fileDownloadLink} alt={file.name}/>
                <div className={styles.fileName}>{file.name}</div>
                <a href={fileDownloadLink}
                   className={classNames(buttons.primaryButton, buttons.buttonLarge, styles.link)}
                   children="download"
                />
            </div>
        </div>
    );
}
