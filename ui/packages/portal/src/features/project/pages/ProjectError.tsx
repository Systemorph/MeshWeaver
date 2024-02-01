import styles from "../layout.module.scss";
import { SideBar } from "../../../shared/components/sideBar/SideBar";
import { NavigateButton } from "../../../shared/components/sideBar/SideBarButtons";
import { NavigateFunction, useNavigate } from "react-router-dom";
import { Button } from "@open-smc/ui-kit/components/Button";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { AxiosError } from "axios";
import page from "../../errorpage.module.scss";
import { SideMenuStoreProvider } from "../../components/sideMenu/SideMenuStore";
import { SideMenu } from "../../components/sideMenu/SideMenu";
import classNames from "classnames";

interface ProjectErrorProps {
    error: unknown;
}

export function ProjectError({error}: ProjectErrorProps) {
    const navigate = useNavigate();

    return (
        <SideMenuStoreProvider>
            <div className={styles.main}>
                <SideMenu>
                    <SideBar>
                        <NavigateButton path={'/'} data-qa-btn-home>
                            <i className="sm sm-systemorph-fill"/>
                        </NavigateButton>
                    </SideBar>
                </SideMenu>
                {getProjectError(error, navigate)}
            </div>
        </SideMenuStoreProvider>
    );
}

// TODO: use links instead of buttons (11/21/2022, akravets)
function getProjectError(error: unknown, navigate: NavigateFunction) {
    const homeButton = (
        <Button className={classNames(button.primaryButton, button.button)} label="Home" onClick={() => navigate('/')}/>
    );

    switch (error) {
        case 404:
            return (
                <div className={page.projectNotFound}>
                    <p title={`${error}: Project not found`} className={page.description}>Sorry, we tried but could not
                        locate the project you requested.</p>
                    {homeButton}
                </div>
            );
        case 403:
            return (
                <div className={page.projectAccessDenied}>
                    <p title={`${error}: Access denied`} className={page.description}>Bummer, it looks like you do not
                        have access to this awesome project yet.</p>
                    {homeButton}
                </div>
            );
        default:
            return (
                <div className={page.projectDefaultError}>
                    <p title={`${error}: Failed to load the project`} className={page.description}>Mercury must be in
                        Retrograde, because we failed to load the project.</p>
                    {homeButton}
                </div>
            );
    }

}