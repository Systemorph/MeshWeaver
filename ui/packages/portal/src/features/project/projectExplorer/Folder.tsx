import { FileModel } from "./projectExplorerStore/fileExplorerState";
import { useGoTo } from "./projectExplorerStore/hooks/useGoTo";
import styles from "./file-list.module.scss";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import classNames from "classnames";
import { useState } from "react";
import { FileNameEditor } from "./FileNameEditor";
import { useFolderMenuItems } from "./projectExplorerStore/hooks/useFolderMenuItems";
import { useProject } from "../projectStore/hooks/useProject";
import { useEnvironmentId } from "../projectStore/hooks/useEnvironmentId";
import { EnvAccessControlApi } from "../envSettings/accessControl/envAccessControlApi";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss";
import PopupMenu from "@open-smc/ui-kit/src/components/PopupMenu";
import Dropdown from "rc-dropdown";

interface FolderProps {
    file: FileModel;
}

export function Folder({file}: FolderProps) {
    const {project} = useProject();
    const envId = useEnvironmentId();
    const goTo = useGoTo();
    const [canEdit, setCanEdit] = useState(false);
    const menuItems = useFolderMenuItems(file, canEdit);

    const popupMenu = (
        <PopupMenu
            className={classNames(styles.menu, 'cls-qa-folder-context-menu')}
            menuItems={menuItems}
        ></PopupMenu>
    );

    return (
        <div className={styles.wrapper}>
            <div className={styles.wrapper} onClick={() => !file.editMode && goTo(file.path)}>
                <i className='sm sm-folder'/>
                {file.editMode
                    ? <FileNameEditor file={file}/>
                    : <span className={styles.link}>{file.name}</span>}
            </div>
            <Dropdown
                trigger={["click"]}
                overlay={popupMenu}
                align={{offset: [-7, -6]}}
            >
                <Button
                    className={classNames(button.button, styles.more)}
                    icon="sm sm-actions-more"
                    onClick={
                        async (event) => {
                            const canEdit = await EnvAccessControlApi.getPermission(project.id, 'Edit', envId, file.id);
                            setCanEdit(canEdit);
                        }
                    }
                    data-qa-btn-context-menu/>
            </Dropdown>
        </div>
    );
}