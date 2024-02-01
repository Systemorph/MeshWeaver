import styles from "./sideMenu.module.scss";
import { isValidElement, PropsWithChildren, ReactNode, useRef } from "react";
import { Lazy } from "../../../shared/components/Lazy";
import classNames from "classnames";
import { useSideMenu } from "./hooks/useSideMenu";
import { SideMenuPanel } from "./SideMenuPanel";

interface SideMenuProps {
    menus?: SideMenus
}

type SideMenus = { [name: string]: ReactNode };

export function SideMenu({children, menus}: PropsWithChildren<SideMenuProps>) {
    const {currentMenu, menuToClose} = useSideMenu();
    const ref = useRef(null);

    const currentIsAdHoc = isValidElement(currentMenu);

    const collection = !menus ? null :
        Object.entries(menus)
            .map(([name, content]) => {
                const lazyState = menuToClose === name ? "none" :
                    currentMenu === name ? "visible" : "hidden";

                return (
                    <Lazy key={name} state={lazyState} className={styles.lazy}>
                        <SideMenuPanel isOpen={lazyState === "visible"}>{content}</SideMenuPanel>
                    </Lazy>
                );
            });

    const isVisible = currentIsAdHoc || (menus && !!menus[currentMenu as string]);

    return (
        <div ref={ref} className={styles.content} data-qa-side-menu>
            {children}
            <Lazy state={isVisible ? 'visible' : 'hidden'} className={styles.lazy}>
                <div className={classNames(styles.overlay)}>
                    {collection}
                    {currentIsAdHoc && currentMenu}
                </div>
            </Lazy>
        </div>
    );
}