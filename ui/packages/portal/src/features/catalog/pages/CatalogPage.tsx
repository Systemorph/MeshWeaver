import styles from '../layout.module.scss';
import { Outlet, useNavigate } from 'react-router-dom'
import { SideMenuStoreProvider } from "../../components/sideMenu/SideMenuStore";
import { CreateProjectForm } from "../../project/editProject/CreateProjectForm";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";
import { SideMenu } from "../../components/sideMenu/SideMenu";
import { CatalogSideBar } from "../components/sideBar/CatalogSideBar";
import { CatalogFooter } from "../components/footer/CatalogFooter";
import { useEffect } from 'react';

export function CatalogContent() {
    const navigate = useNavigate();
    const {closeMenu, hideMenu} = useSideMenu();

    const menus = {
        'create': <CreateProjectForm
            onClose={hideMenu}
            onCancel={() => closeMenu()}
            onCreated={(project) => {
                closeMenu();
                navigate(`/project/${project.id}`);
            }}/>
    }

    useEffect(() => {
        document.title = "Systemorph Cloud";
    }, [])

    return (
        <div className={styles.main}>
            <SideMenu menus={menus}>
                <CatalogSideBar/>
            </SideMenu>
            <div className={styles.content}>
                <Outlet/>
            </div>
        </div>
    );
}

export function CatalogPage() {
    return (
        <>
            <SideMenuStoreProvider>
                <CatalogContent/>
            </SideMenuStoreProvider>
            <CatalogFooter/>
        </>
    );
}