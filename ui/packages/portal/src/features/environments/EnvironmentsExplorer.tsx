import { EnvironmentCard } from "./EnvironmentCard";
import styles from "./environments.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { map } from "lodash";
import { Button, ButtonProps } from "@open-smc/ui-kit/components/Button";
import { CreateEnvironmentForm } from "./CreateEnvironmentForm";
import { useState } from "react";
import { useProject } from "../project/projectStore/hooks/useProject";
import { useNavigate } from "react-router-dom";
import classNames from "classnames";
import { FormHeader } from "../../shared/components/sideMenuComponents/FormHeader";
import { useEnv } from "../project/projectStore/hooks/useEnv";
import { useProjectPermissions } from "../project/projectStore/hooks/useProjectPermissions";
import { useSideMenu } from "../components/sideMenu/hooks/useSideMenu";

export function EnvironmentsExplorer() {
    const {project: {id: projectId, environments}, reloadProject} = useProject();
    const [isBottomItem, setIsBottomItem] = useState<boolean>(false);
    const {envId: currentEnvId} = useEnv();
    const navigate = useNavigate();
    const {isOwner} = useProjectPermissions();
    const {hideMenu} = useSideMenu();

    const AddNewButton = ({className, ...props}: ButtonProps) => {
        return <Button icon={'sm sm-plus-circle'}
                       className={classNames(styles.addNew, button.button, className)}
                       onClick={() => setIsBottomItem(!isBottomItem)} {...props}/>;
    }

    return (
        <div className={styles.environments} data-qa-environments>
            <FormHeader onClose={hideMenu} text={'Environments'} button={isOwner && <AddNewButton data-qa-btn-add/>}/>

            <div className={styles.cards}>
                {
                    map(
                        environments,
                        envId =>
                            <EnvironmentCard
                                key={envId}
                                isActive={envId === currentEnvId}
                                environmentId={envId}
                            />
                    )
                }

                {isOwner && isBottomItem && <CreateEnvironmentForm
                    projectId={projectId}
                    onCreated={async (environmentId) => {
                        await reloadProject();
                        navigate(`/project/${projectId}/env/${environmentId}`);
                        setIsBottomItem(false);
                    }}
                    onCancel={() => setIsBottomItem(false)}/>}
                {isOwner && !isBottomItem &&
                    <AddNewButton className={classNames(styles.addButtonBig, button.button)} data-qa-btn-add-big/>}
            </div>
        </div>
    );
}