import styles from "./cards.module.scss";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { CreateProjectForm } from "../../../project/editProject/CreateProjectForm";
import { useSideMenu } from "../../../components/sideMenu/hooks/useSideMenu";
import { useNavigate } from "react-router-dom";
import classNames from "classnames";

export function EmptyProjectCollection() {
    const {showMenu, closeMenu} = useSideMenu();
    const navigate = useNavigate();

    return (
        <div className={styles.emptyCard}>
            <p>It is getting lonely in here, let's open some projects or add our own!</p>
            <Button
                className={classNames(button.primaryButton, button.button)}
                label="New project"
                icon="sm sm-plus"
                onClick={() => {
                    showMenu(
                        <CreateProjectForm
                            onClose={closeMenu}
                            onCancel={closeMenu}
                            onCreated={({id}) => {
                                closeMenu();
                                navigate(`/project/${id}`);
                            }}/>
                    )
                }}
            />
        </div>
    )
}