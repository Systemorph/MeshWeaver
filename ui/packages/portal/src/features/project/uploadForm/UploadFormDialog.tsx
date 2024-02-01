import dialogStyles from "./dialog.module.scss";
import {useRef} from "react";
import { Dialog } from "primereact/dialog";
import classNames from "classnames";
import { UploadForm } from "../uploadForm/UploadForm";
import { useOnClickOutside } from "usehooks-ts";
import { useUploadDialog } from "../projectExplorer/projectExplorerStore/hooks/useUploadDialog";
import { UploadFormStore } from "./store";
import { useKeepUploadFormOpen } from "../projectExplorer/projectExplorerStore/hooks/useKeepUploadFormOpen";

export function UploadFormDialog() {
    const uploadFormRef = useRef(null);
    const {uploadFormVisible, closeUploadForm} = useUploadDialog();
    const {keepUploadFormOpen} = useKeepUploadFormOpen();

    const onUploadFormClose = () => {
        closeUploadForm();
    }

    useOnClickOutside(uploadFormRef.current?.dialogRef, () => {
        if(!keepUploadFormOpen) {
            onUploadFormClose();
        }
    });

    return (
            <Dialog
                header={<span data-qa-title>Upload files</span>}
                closable={false}
                ref={uploadFormRef}
                className={classNames(dialogStyles.dialog, 'cls-qa-dialog-upload')}
                visible={uploadFormVisible}
                onShow={null}
                onHide={null}
            >
                <UploadFormStore>
                    <UploadForm />
                </UploadFormStore>
            </Dialog>
    );
}