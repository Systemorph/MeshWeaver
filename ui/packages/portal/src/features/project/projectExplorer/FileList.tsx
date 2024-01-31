import styles from "./file-list.module.scss";
import { useFileIds } from "./projectExplorerStore/hooks/useFileIds";
import { FileListItem } from "./FileListItem";
import { useActiveFile } from "../projectStore/hooks/useActiveFile";

export function FileList() {
    const fileIds = useFileIds();
    const {activeFile} = useActiveFile();

    const renderedItemList = fileIds.map(fileId => {
        return (
            <FileListItem
                fileId={fileId}
                isActive={fileId === activeFile?.id}
                key={fileId}/>
        );
    });

    if (renderedItemList.length === 0) {
        return null;
    }

    return (
        <ul className={styles.list} data-qa-node-list>
            {renderedItemList}
        </ul>
    );
}