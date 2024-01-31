import { useDropzone } from "react-dropzone";
import { UploadFormFile } from "./UploadFormFile";
import { Button } from "@open-smc/ui-kit/components/Button";
import { useUploadFormFileIds } from "./hooks/useUploadFormFileIds";
import { useAddUploadFormFiles } from "./hooks/useAddUploadFormFiles";
import { useCancelAll } from "./hooks/useCancelAll";
import { useEffect } from "react";
import styles from "./upload-form.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { useKeyPress } from "../../../shared/hooks/useKeyPress";
import { useSelectFiles } from "./hooks/useSelectFiles";
import { useUploadDialog } from "../projectExplorer/projectExplorerStore/hooks/useUploadDialog";
import { useKeepUploadFormOpen } from "../projectExplorer/projectExplorerStore/hooks/useKeepUploadFormOpen";
import classNames from "classnames";

export function UploadForm() {
    const fileIds = useUploadFormFileIds();
    const activeFiles = useSelectFiles(({status}) => status === 'New' || status === 'InProgress');
    const addFiles = useAddUploadFormFiles();
    const cancelAll = useCancelAll();
    const {closeUploadForm} = useUploadDialog();
    const {keepUploadFormOpen, setKeepUploadFormOpen} = useKeepUploadFormOpen();

    const escapeCallback = () => {
        if (!keepUploadFormOpen) {
            closeUploadForm();
        }
    }

    useEffect(() => {
        setKeepUploadFormOpen(activeFiles?.length > 0);
    }, [activeFiles.length]);

    useEffect(() => cancelAll, []);

    useKeyPress(null, escapeCallback, 'Escape');

    const {getRootProps, getInputProps, open, isDragActive} = useDropzone({
        onDrop: addFiles,
        noClick: true,
        noKeyboard: true
    });

    const renderedFiles = fileIds.map(fileId => <UploadFormFile fileId={fileId} key={fileId}/>);

    const hasFiles = renderedFiles.length > 0;

    return (
        <div className={styles.wrapper} data-qa-upload-area>
            <div className={styles.drugDropContainer}>
                <div
                    {...getRootProps()}
                    className={`${styles.upload} ${isDragActive ? styles.dragging : ''} ${hasFiles ? styles.hasFiles : ''}`}>
                    <input {...getInputProps()} data-qa-input/>
                    <p className={styles.drugDrop}>
                        Drag&drop files here
                    </p>
                    <span className={styles.text}>or</span>
                    <Button className={classNames(styles.browse, button.button)}
                            icon={'sm sm-shared'}
                            onClick={open}
                            data-qa-btn-browse
                            label="Browse files"/>
                </div>
            </div>

            {
                hasFiles &&
                <div className={styles.files} data-qa-uploaded-files>
                    <ul className={styles.list}>{renderedFiles}</ul>
                </div>
            }
            <div className={styles.actions}>
                <Button className={classNames(button.primaryButton, button.button, styles.done)}
                        label="Done"
                        disabled={activeFiles.length > 0} onClick={closeUploadForm}
                        data-qa-btn-close/>
                {activeFiles.length > 1 &&
                    <Button className={classNames(button.cancelButton, button.button)}
                            type="button"
                            onClick={cancelAll}
                            label="Cancel all"
                            icon={'sm sm-close'}/>
                }
            </div>
        </div>
    )
}