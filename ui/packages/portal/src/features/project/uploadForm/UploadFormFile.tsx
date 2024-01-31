import React, { useState } from "react";
import { humanFileSize } from "../../../shared/utils/helpers";
import { Button } from "@open-smc/ui-kit/components/Button";
import { ProgressBar } from "primereact/progressbar";
import { useUploadFormFile } from "./hooks/useUploadFormFile";
import { useCancel } from "./hooks/useCancel";
import styles from "./upload-file.module.scss";
import buttons from "@open-smc/ui-kit/components/buttons.module.scss"
import "./progress-bar.scss"
import { Path } from "../../../shared/utils/path";
import { Checkbox } from "primereact/checkbox";
import { useResolveConflict } from "./hooks/useResolveConflict";
import { useRetryUpload } from "./hooks/useRetryUpload";
import { ConflictResolution } from "./store";
import { useSelectFiles } from "./hooks/useSelectFiles";
import classNames from "classnames";
import button from "@open-smc/ui-kit/components/buttons.module.scss"

interface UploadFileProps {
    fileId: string;
}

export function UploadFormFile({fileId}: UploadFileProps) {
    const {blob, conflict, conflictResolution, status, progressEvent} = useUploadFormFile(fileId);
    const retryUpload = useRetryUpload();
    const resolveConflict = useResolveConflict();
    const conflicts = useSelectFiles(
        ({status, conflict, conflictResolution}) =>
            status === 'New' && conflict && !conflictResolution
    );
    const cancel = useCancel();

    const fileName = blob.name;

    const overwrite = (applyToAll: boolean) => resolveConflict(fileId, 'Overwrite', applyToAll);

    const renderedFile = (function () {
        if (status === 'New') {
            if (conflict) {
                return <Conflict fileName={fileName}
                                 fileId={fileId}
                                 onOverwrite={overwrite}
                                 enableApplyToAll={conflicts.length > 1}
                                 onCancel={() => cancel(fileId)}/>;
            }
            return <InProgressFile fileName={fileName}/>
        }
        if (status === 'InProgress') {
            return <InProgressFile fileName={fileName} progressEvent={progressEvent} onCancel={() => cancel(fileId)}/>
        }
        if (status === 'Complete') {
            return <CompletedFile fileName={fileName}
                                  size={progressEvent.total}
                                  conflict={conflict}
                                  conflictResolution={conflictResolution}/>
        }
        if (status === 'Canceled') {
            return <ErroredFile fileName={fileName} error={'Canceled'} onRetry={() => retryUpload(fileId)}/>;
        }
        if (status === 'Error') {
            return <ErroredFile fileName={fileName} error={'Upload failed'} onRetry={() => retryUpload(fileId)}/>;
        }
        throw 'Unknown status';
    })();

    const className = status === 'Canceled' || status === 'Error' ? styles.errorItem : (status === 'New' && conflict ? styles.conflictItem : styles.item)

    return renderedFile &&
        <li className={className}>
            {renderedFile}
        </li>

}

interface ConflictProps {
    fileName: string;
    fileId: string;
    onOverwrite: (applyToAll: boolean) => void;
    enableApplyToAll: boolean;
    onCancel: () => void;
}

function Conflict({fileName, fileId, onOverwrite, enableApplyToAll, onCancel}: ConflictProps) {
    const [applyToAll, setApplyToAll] = useState(false)

    return (
        <div className={classNames(styles.conflict)}>
            <div className={styles.fileWrapper}>
                <FileName fileName={fileName}/>
                <div className={styles.status}>
                    <i className="sm sm-alert"/>
                </div>
            </div>
            <div className={styles.messageBox}>
                <p className={styles.formText}>A file with this name already exists.</p>
                <p className={styles.formText}>Would you like to replace the existing file?</p>
            </div>
            <div className={styles.buttonsBox}>
                <Button type="button"
                        label='Replace'
                        onClick={() => onOverwrite(applyToAll)}
                        className={classNames(buttons.primaryButton, buttons.buttonSmall, buttons.button)}/>
                <Button type="button"
                        label='Cancel'
                        icon="sm sm-close"
                        onClick={onCancel}
                        className={classNames(buttons.cancelButton, buttons.button)}/>
                {enableApplyToAll &&
                    <div className={styles.checkboxBox}>
                        <Checkbox inputId={`applyToAll-${fileId}`} checked={applyToAll}
                                  onChange={e => setApplyToAll(e.checked)}/>
                        <label className={styles.checkboxLabel} htmlFor={`applyToAll-${fileId}`}>apply to all
                            conflicts</label>
                    </div>
                }
            </div>
        </div>
    );
}

interface ErroredFileProps {
    fileName: string;
    error: string;
    onRetry: () => void;
}

function ErroredFile({fileName, error, onRetry}: ErroredFileProps) {
    return (
        <div className={styles.fileWrapper}>
            <FileName fileName={fileName}/>
            <div className={styles.status} data-qa-fail>
                <span>{error}</span>
                <Button icon={'sm sm-refresh'} onClick={onRetry} className={classNames(styles.retry, buttons.button)}/>
            </div>
        </div>
    );
}

interface CompletedFileProps {
    fileName: string;
    conflict: boolean;
    conflictResolution: ConflictResolution;
    size: number;
}

function CompletedFile({fileName, size, conflict, conflictResolution}: CompletedFileProps) {
    return (
        <div className={styles.fileWrapper}>
            <FileName fileName={fileName}/>
            <div className={styles.status} data-qa-success>
                {conflict &&
                    <span
                        className={conflictResolution === 'Overwrite' && 'Overwritten' ? styles.overwrite : styles.copy}>
                    {conflictResolution === 'Overwrite' ? 'Overwritten' : 'Saved as copy'}
                </span>}
                <span className={styles.complete}>
                    {humanFileSize(size)}
                    <i className="sm sm-check"/>
                </span>
            </div>
        </div>
    );
}

interface InProgressFileProps {
    fileName: string;
    progressEvent?: ProgressEvent;
    onCancel?: () => void;
}

function InProgressFile({fileName, progressEvent, onCancel}: InProgressFileProps) {
    const getProgressValue = () => progressEvent ? Math.round(progressEvent.loaded * 100 / progressEvent.total) : 0;

    return (
        <>
            <div className={styles.fileWrapper}>
                <FileName fileName={fileName}/>
                {progressEvent &&
                    <div className={styles.status}>
                        <span>{humanFileSize(progressEvent.loaded)} of {humanFileSize(progressEvent.total)}</span>
                        <Button className={classNames(styles.cancel, button.button)}
                                onClick={onCancel}
                                icon={'sm sm-close'}/>
                    </div>
                }
            </div>
            {progressEvent &&
                <div className={styles.progressbarWrapper}>
                    <ProgressBar value={getProgressValue()}/>
                </div>
            }
        </>
    );
}

interface FileNameProps {
    fileName: string;
}

function FileName({fileName}: FileNameProps) {
    const ext = Path.extname(fileName);
    const name = fileName.slice(0, fileName.length - ext.length);

    return (
        <div className={styles.nameWrapper}>
            <i className='sm sm-document'/>
            <div className={styles.name}>
                <span className={styles.fileName}>{name}</span>
                <span className={styles.fileExtension}>{ext}</span>
            </div>
        </div>
    );
}