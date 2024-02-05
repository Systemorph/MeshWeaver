import styles from "./environments.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { useNavigate } from "react-router-dom";
import { useProject } from "../project/projectStore/hooks/useProject";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import classNames from "classnames";
import { useState } from "react";
import { CloneProjectForm } from "../project/editProject/CloneProjectForm";
import { useSideMenu } from "../components/sideMenu/hooks/useSideMenu";
import { DuplicateEnvironmentForm } from "./DuplicateEnvironmentForm";
import { DeleteEnvironmentForm } from "./DeleteEnvironmentForm";
import { useProjectPermissions } from "../project/projectStore/hooks/useProjectPermissions";
import { ProjectApi } from "../../app/projectApi";
import PopupMenu, { PopupMenuItem } from "@open-smc/ui-kit/src/components/PopupMenu";
import Dropdown from "rc-dropdown";

type Props = {
    environmentId: string;
    isActive: boolean;
};

type FormProps = Props & {
    setMode: (mode: Mode) => void;
};

type Mode = 'duplicate' | 'delete' | null;

const RegularCard = ({
                         environmentId,
                         setMode,
                         isActive,
                     }: FormProps) => {
    const {project: {id: projectId, defaultEnvironment}} = useProject();
    const navigate = useNavigate();
    const {showMenu, closeMenu, hideMenu} = useSideMenu();
    const {isOwner: isProjectOwner} = useProjectPermissions();
    const [isCloning, setIsCloning] = useState<boolean>();

    const menuItems: PopupMenuItem[] = [
        {
            label: 'Settings',
            icon: 'sm sm-settings',
            className: styles.item,
            qaAttribute: 'data-qa-btn-settings',
            onClick: () => navigate(`/project/${projectId}/env-settings/${environmentId}`)
        },
        {
            label: 'Access Control',
            icon: 'sm sm-lock',
            className: styles.item,
            qaAttribute: 'data-qa-btn-access',
            onClick: () => navigate(`/project/${projectId}/env-settings/${environmentId}/access`)
        },
        {
            label: 'Duplicate',
            icon: 'sm sm-paste',
            className: styles.item,
            qaAttribute: 'data-qa-btn-duplicate',
            onClick: () => setMode('duplicate'),
            disabled: !isProjectOwner || isCloning !== false
        },
        {
            label: 'Clone',
            icon: 'sm sm-copy',
            className: styles.item,
            qaAttribute: 'data-qa-btn-clone',
            disabled: isCloning !== false,
            onClick: () => {
                showMenu(<CloneProjectForm
                    projectId={projectId}
                    environment={environmentId}
                    onClose={closeMenu}
                    onCancel={closeMenu}
                    onFinish={
                        (id) => {
                            closeMenu();
                            navigate(`/project/${id}`);
                        }
                    }
                />);
            }
        },

        {
            label: 'Delete',
            icon: 'sm sm-trash',
            className: styles.item,
            qaAttribute: 'data-qa-btn-delete',
            onClick: () => setMode('delete'),
            hidden: defaultEnvironment === environmentId,
            disabled: !isProjectOwner || isCloning !== false
        }
    ];

    const popupMenu = (
        <PopupMenu
            className={classNames(styles.menu, 'cls-qa-environment-context-menu')}
            menuItems={menuItems}
        ></PopupMenu>
    );

    return <>
        <div
            className={`${styles.card} ${isActive ? styles.active : ''}`}
            onClick={() => navigate(`/project/${projectId}/env/${environmentId}`)}
            data-qa-env-id={environmentId}>
            <span data-qa-name>{environmentId}</span>
        </div>

        <Dropdown
                    trigger={["click"]}
                    overlay={popupMenu}
                    align={{offset: [-7, -5]}}
                >
        <Button
            className={classNames(styles.more, button.button)}
            icon="sm sm-actions-more"
            onClick={async (event) => {
                const {isCloning} = await ProjectApi.getEnv(projectId, environmentId);
                setIsCloning(isCloning);
            }}
            data-qa-btn-context-menu
        />
        </Dropdown>
    </>
}

const DuplicateForm = ({environmentId, setMode}: Omit<FormProps, 'isActive'>) => {
    const {project: {id: projectId}, reloadProject} = useProject();
    const navigate = useNavigate();

    return <DuplicateEnvironmentForm
        projectId={projectId}
        environmentId={environmentId}
        onDuplicated={async (newEnvId) => {
            await reloadProject();
            navigate(`/project/${projectId}/env/${newEnvId}`);
            setMode(null);
        }}
        onCancel={() => setMode(null)}
    />
}

const DeleteForm = ({
                        environmentId,
                        setMode,
                        isActive
                    }: FormProps) => {
    const {project: {id: projectId, defaultEnvironment}, reloadProject} = useProject();
    const navigate = useNavigate();
    return <DeleteEnvironmentForm
        projectId={projectId}
        environmentId={environmentId}
        onDeleted={async () => {
            isActive && navigate(`/project/${projectId}/env/${defaultEnvironment}`);
            await reloadProject();
            setMode(null);
        }}
        onCancel={() => setMode(null)}
    />
}

export function EnvironmentCard({environmentId, isActive}: Props) {
    const [mode, setMode] = useState<Mode>();

    return (
        <div className={styles.envContainer}>
            {mode === 'delete' ?
                <DeleteForm setMode={setMode} environmentId={environmentId} isActive={isActive}/> :
                <RegularCard setMode={setMode} environmentId={environmentId} isActive={isActive}/>}
            {mode === 'duplicate' && <DuplicateForm environmentId={environmentId} setMode={setMode}/>}
        </div>
    );
}