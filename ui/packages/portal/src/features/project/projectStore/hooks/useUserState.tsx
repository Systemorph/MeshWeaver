import { useSideMenu } from "../../../components/sideMenu/hooks/useSideMenu";
import { useEffect } from "react";
import { projectMenuNames } from "../../components/sideBar/ProjectSideBar";
import { isString } from "lodash";
import { useProject } from "./useProject";

const NO_MENU_VALUE = 'None';

export function useUserState() {
    const {project} = useProject();
    const {currentMenu, showMenu} = useSideMenu();

    const currentMenuKey = `${project.id}.currentMenu`;

    useEffect(() => {
        const currentMenu = window.sessionStorage.getItem(currentMenuKey);

        if (currentMenu !== NO_MENU_VALUE) {
            showMenu(currentMenu || projectMenuNames.explorer);
        }
    }, []);

    useEffect(() => {
        window.sessionStorage.setItem(currentMenuKey, currentMenu && isString(currentMenu) ? currentMenu : NO_MENU_VALUE);
    }, [currentMenu]);
}