import { useUploadDialog } from "./projectExplorerStore/hooks/useUploadDialog";
import { useAddNewFile } from "./projectExplorerStore/hooks/useAddNewFile";
import { useFileExplorerSelector } from "./ProjectExplorerContextProvider";
import PopupMenu, { PopupMenuItem } from "@open-smc/ui-kit/src/components/PopupMenu";
import classNames from "classnames";
import { FormHeader } from "../../../shared/components/sideMenuComponents/FormHeader";
import Dropdown from "rc-dropdown";
import { Button } from "@open-smc/ui-kit/components";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss";
import styles from "./project-explorer.module.scss";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";

export function ProjectExplorerHeader() {
    const {openUploadForm} = useUploadDialog();
    const addNewFile = useAddNewFile();
    const canEdit = useFileExplorerSelector("canEdit");
    const {hideMenu} = useSideMenu();

    const menuItems: PopupMenuItem[] = [
        {
            label: 'Create notebook',
            icon: 'sm sm-overview',
            qaAttribute: 'data-qa-btn-create-notebook',
            onClick: () => {
                return addNewFile('Notebook');
            },
            disabled: !canEdit
        },
        {
            label: 'Create folder',
            icon: 'sm sm-folder',
            qaAttribute: 'data-qa-btn-create-folder',
            onClick: () => {
                return addNewFile('Folder');
            },
            disabled: !canEdit
        },
        {
            label: 'Upload file(s)',
            icon: 'sm sm-upload-files',
            qaAttribute: 'data-qa-btn-upload',
            onClick: () => {
                openUploadForm();
            },
            disabled: !canEdit
        }
    ];

    const popupMenu = (
        <PopupMenu
            className={classNames('cls-qa-explorer-context-menu')}
            menuItems={menuItems}
        />
    );

    return (
        <FormHeader
            text={'Explorer'}
            onClose={hideMenu}
            button={canEdit &&
                <Dropdown
                    trigger={["click"]}
                    overlay={popupMenu}
                    align={{offset: [5, -10]}}
                >
                    <Button
                        className={classNames(button.button, styles.more,)}
                        icon="sm sm-actions-more"
                        data-qa-btn-context-menu
                    />
                </Dropdown>
            }/>
    );
}