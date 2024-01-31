import { SideMenu, SideMenuState } from "../SideMenuState";
import { useSelector, useStore } from "../SideMenuStore";
import { isValidElement } from "react";

const menuToCloseSelector = (state: SideMenuState) => state.menuToClose;
const currentSelector = (state: SideMenuState) => state.currentMenu;

export function useSideMenu() {
    const {getState, notify, setState} = useStore();
    const currentMenu = useSelector(currentSelector);
    const menuToClose = useSelector(menuToCloseSelector);

    const showMenu = (newMenu: SideMenu, closePrevious: boolean = false) => {
        const {currentMenu: previousMenu} = getState();
        const newIsAdHoc = isValidElement(newMenu);
        const menuToClose = closePrevious && !newIsAdHoc ? previousMenu : null;
        setState({currentMenu: newMenu, menuToClose});
        notify(currentSelector);
    }

    const toggleMenu = (newCurrent: string) => {
        const {currentMenu} = getState();
        showMenu(currentMenu !== newCurrent ? newCurrent : null);
    }

    const hideMenu = () => {
        showMenu(null);
    }

    const closeMenu = () => {
        showMenu(null, true);
    }

    return {
        currentMenu,
        menuToClose,
        showMenu,
        toggleMenu,
        hideMenu,
        closeMenu,
    };
}