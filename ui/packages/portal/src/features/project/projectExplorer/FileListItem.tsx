import { useFile } from "./projectExplorerStore/hooks/useFile";
import { Folder } from "./Folder";
import { File } from "./File";
import styles from "./file-list.module.scss";

interface FileListItemProps {
    fileId: string;
    isActive?: boolean;
}

export function FileListItem({fileId, isActive}: FileListItemProps) {
    const file = useFile(fileId);

    const renderedContent = file.kind === 'Folder'
        ? <Folder file={file}/>
        : <File file={file}/>;

    return (
        <li
            className={`${styles.item} ${isActive ? styles.active : ''} ${file.editMode ? styles.editMode : ''}`}
            data-qa-node-type={file.kind}
            data-qa-node={file.name}>
            {renderedContent}
        </li>
    );
}