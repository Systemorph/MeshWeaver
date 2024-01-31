import { useNavigate } from "react-router-dom";
import { useProject } from "../../projectStore/hooks/useProject";
import { useSideMenu } from "../../../components/sideMenu/hooks/useSideMenu";
import { MenuItem, MenuLabel, NavigateButton } from "../../../../shared/components/sideBar/SideBarButtons";
import { ButtonGroup, ButtonGroups, SideBar } from "../../../../shared/components/sideBar/SideBar";
import { useEnv } from "../../projectStore/hooks/useEnv";

export const projectMenuNames = {
    overview: "overview",
    environments: "envrs",
    git: "git",
    explorer: "explorer",
    search: "search",
    logs: "logs",
    settings: "settings",
    account: "account"
}

export function ProjectSideBar() {
    const {project} = useProject();
    const {env} = useEnv();
    const navigate = useNavigate();
    const {currentMenu, toggleMenu} = useSideMenu();

    return (
        <SideBar>
            <NavigateButton path={'/'} data-qa-btn-home>
                <i className="sm sm-systemorph-fill"/>
            </NavigateButton>

            <ButtonGroups>
                <ButtonGroup>
                    <MenuItem
                        onClick={() => navigate(`/project/${project.id}/env/${env.id}`)}
                        disabled={!env}
                        data-qa-btn-overview
                    >
                        <i className="sm sm-overview"/>
                        <MenuLabel small text={projectMenuNames.overview}/>
                    </MenuItem>

                    <MenuItem
                        active={currentMenu === projectMenuNames.explorer}
                        onClick={() => toggleMenu(projectMenuNames.explorer)}
                        data-qa-btn-explorer
                    >
                        <i className="sm sm-explorer"/>
                        <MenuLabel small text={projectMenuNames.explorer}/>
                    </MenuItem>

                    <MenuItem
                        active={currentMenu === projectMenuNames.environments}
                        onClick={() => toggleMenu(projectMenuNames.environments)}
                        data-qa-btn-envrs
                    >
                        <i className="sm sm-enviroment"/>
                        <MenuLabel small text={projectMenuNames.environments}/>
                    </MenuItem>
                </ButtonGroup>
                <ButtonGroup>
                    <MenuItem
                        active={currentMenu === projectMenuNames.settings}
                        onClick={() => navigate(`/project/${project.id}/settings`)}
                        data-qa-btn-settings
                    >
                        <i className="sm sm-settings"/>
                        <MenuLabel small text={projectMenuNames.settings}/>
                    </MenuItem>
                </ButtonGroup>
            </ButtonGroups>
        </SideBar>
    );
}