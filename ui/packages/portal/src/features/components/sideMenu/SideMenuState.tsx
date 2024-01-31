import { ReactNode } from "react";

export type SideMenu = string | ReactNode | null

export type SideMenuState = {
    currentMenu?: SideMenu,
    menuToClose?: SideMenu,
    keepOpen?: boolean;
};