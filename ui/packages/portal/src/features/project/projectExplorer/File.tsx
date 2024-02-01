import { Link } from "react-router-dom";
import { FileModel } from "./projectExplorerStore/fileExplorerState";
import styles from "./file-list.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { Button } from "@open-smc/ui-kit/components/Button";
import classNames from "classnames";
import { useEnvironmentId } from "../projectStore/hooks/useEnvironmentId";
import { useProject } from "../projectStore/hooks/useProject";
import { useState } from "react";
import { FileNameEditor } from "./FileNameEditor";
import { useFileMenuItems } from "./projectExplorerStore/hooks/useFileMenuItems";
import { EnvAccessControlApi } from "../envSettings/accessControl/envAccessControlApi";
import PopupMenu from "@open-smc/ui-kit/components/PopupMenu";
import Dropdown from "rc-dropdown";
interface FileProps {
    file: FileModel;
}

export function File({file}: FileProps) {
    const {project} = useProject();
    const envId = useEnvironmentId();
    const [canEdit, setCanEdit] = useState(false);
    const menuItems = useFileMenuItems(file, canEdit);

    const link = `/project/${project.id}/env/${envId}/${file.path}`;

    const popupMenu = (
        <PopupMenu
            className={classNames(styles.menu, 'cls-qa-file-context-menu')}
            menuItems={menuItems}
        ></PopupMenu>
    );
    // TODO: make icon clickable (4/15/2022, akravets)
    return (
        <div className={styles.wrapper}>
            <i className={`sm sm-${file.kind === "Notebook" ? "overview" : "document"}`}/>
            {file.editMode
                ? <FileNameEditor file={file}/>
                : <Link to={link} className={styles.link} children={file.name}/>}
            <Dropdown
                trigger={["click"]}
                overlay={popupMenu}
                align={{offset: [-7, -6]}}
            >
                <Button
                    className={classNames(button.button, styles.more)}
                    icon="sm sm-actions-more"
                    onClick={async (event) => {
                        const canEdit = await EnvAccessControlApi.getPermission(
                            project.id,
                            "Edit",
                            envId,
                            file.id
                        );
                        setCanEdit(canEdit);
                    }}
                    data-qa-btn-context-menu
                />
            </Dropdown>
        </div>
    );
}