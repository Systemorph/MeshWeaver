import { useState, KeyboardEvent, useEffect, useRef } from "react";
import { trim } from "lodash";
import { useMoveFile } from "./projectExplorerStore/hooks/useMoveFile";
import { useSaveNewFile } from "./projectExplorerStore/hooks/useSaveNewFile";
import { useReloadFiles } from "./projectExplorerStore/hooks/useReloadFiles";
import { FileModel } from "./projectExplorerStore/fileExplorerState";
import { Path } from "../../../shared/utils/path";
import styles from "./file-list.module.scss";
import { useGetUniqueFileName } from "./projectExplorerStore/hooks/useGetUniqueFileName";
import Input from 'rc-input';
import { useFileExplorerStore, useFileStore } from "./ProjectExplorerContextProvider";

interface FileNameEditorProps {
    file: FileModel;
}

export function FileNameEditor({file}: FileNameEditorProps) {
    const fileExplorerStore = useFileExplorerStore();
    const fileStore = useFileStore();
    const moveFile = useMoveFile();
    const saveNewFile = useSaveNewFile();
    const reloadFiles = useReloadFiles();
    const getUniqueFileName = useGetUniqueFileName();

    const onFinishEditing = async (name: string) => {
        name = getUniqueFileName(name);

        fileExplorerStore.setState(state => {
            state.loading = true;
        });

        fileStore.setState(files => {
            files[file.id].name = name;
            files[file.id].editMode = false;
        });

        if (file.actualFileId) {
            const newPath = Path.dirname(file.path) ? Path.join(Path.dirname(file.path), name) : name;
            await moveFile(file.id, newPath);
        }
        else {
            await saveNewFile(file.id);
        }

        await reloadFiles();

        fileExplorerStore.setState(state => {
            state.loading = false;
        });
    }

    const onCancelEditing = async () => {
        if (!file.actualFileId) {
            fileExplorerStore.setState(state => {
                state.loading = true;
            });

            const name = getUniqueFileName(file.kind === 'Folder' ? 'New folder' : 'New notebook');

            fileStore.setState(files => {
                files[file.id].name = name;
                files[file.id].editMode = false;
            });

            await saveNewFile(file.id);

            await reloadFiles();

            fileExplorerStore.setState(state => {
                state.loading = false;
            });
        }
        else {
            fileStore.setState(files => {
                files[file.id].editMode = false;
            });
        }
    }

    return <EditorInput value={file.name} onFinish={onFinishEditing} onCancel={onCancelEditing}/>;
}

interface EditorInputProps {
    value: string;
    onFinish: (newValue: string) => void;
    onCancel: () => void;
}

function EditorInput({value, onFinish, onCancel}: EditorInputProps) {
    const [newValue, setNewValue] = useState(value);
    const ref = useRef(null);
    
    const tryFinish = () => {
        const name = trim(newValue);

        if (name && name !== value) {
            onFinish(name);
        }
        else {
            cancel();
        }
    }

    const cancel = () => onCancel();

    useEffect(() => {
        ref.current.focus();
        ref.current.select();
    }, []);

    const onKeydown = (event: KeyboardEvent<HTMLInputElement>) => {
        if (event.key === 'Enter') {
            tryFinish();
        }
        else if (event.key === 'Escape') {
            cancel();
            event.stopPropagation();
        }
    }

    return (
        <Input 
            ref={ref}
            className={styles.rename}
            value={newValue}
            onChange={event => setNewValue(event.target.value)}
            onKeyDown={onKeydown}
            // TODO: Temporary decision, useOnClickOutside hook doesn't work properly while renaming multiple options (5/20/2022, avonokurov)
            onBlur={tryFinish}
        />
    );
}