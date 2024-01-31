import { Link, Outlet } from "react-router-dom";
import React from "react";
import styles from "./mainLayout.module.scss";

export function MainLayout() {
    return (
        <div className={styles.container}>
            <header className={styles.header}></header>
            <div className={styles.sidebar}>
                <h2 className={styles.sidebarTitle}>
                    Components
                </h2>
                <ul className={styles.componentsList}>
                    <li className={styles.componentsItem}><Link to={"/"}>Home</Link></li>
                    <li className={styles.componentsItem}><Link to={"notebook-editor"}>NotebookEditor</Link></li>
                    <li className={styles.componentsItem}><Link to={"menu-item"}>MenuItem</Link></li>
                    <li className={styles.componentsItem}><Link to={"multiselect"}>Multiselect</Link></li>
                    <li className={styles.componentsItem}><Link to={"classification"}>Classification</Link></li>
                    <li className={styles.componentsItem}><Link to={"grid"}>Grid</Link></li>
                    <li className={styles.componentsItem}><Link to={"title"}>Title</Link></li>
                    <li className={styles.componentsItem}><Link to={"textbox"}>Textbox</Link></li>
                    <li className={styles.componentsItem}><Link to={"number"}>Number</Link></li>
                    <li className={styles.componentsItem}><Link to={"item-template"}>ItemTemplate</Link></li>
                    <li className={styles.componentsItem}><Link to={"badge"}>Badge</Link></li>
                    <li className={styles.componentsItem}><Link to={"chart"}>Chart</Link></li>
                    <li className={styles.componentsItem}><Link to={"activity"}>Activity</Link></li>
                    <li className={styles.componentsItem}><Link to={"html"}>HTML</Link></li>
                    <li className={styles.componentsItem}><Link to={"icon"}>Icon</Link></li>
                    <li className={styles.componentsItem}><Link to={"checkbox"}>Checkbox</Link></li>
                    <li className={styles.componentsItem}>
                        <Link to={"main-window"}>MainWindow</Link> <Link to={"/smapp"} title={"Full screen"}><i className={"sm sm-external-link"}/></Link>
                    </li>
                    <li className={styles.componentsItem}><Link to={"spinner"}>Spinner</Link></li>
                    <li className={styles.componentsItem}><Link to={"grid-layout"}>GridLayout</Link></li>
                    <li className={styles.componentsItem}><Link to={"layout"}>Layout</Link></li>
                    <li className={styles.componentsItem}><Link to={"modal"}>Modal</Link></li>
                </ul>
            </div>
            <div className={styles.content}>
                <Outlet/>
            </div>
        </div>
    );
}