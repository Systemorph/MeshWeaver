import page from "./errorpage.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { Button } from "@open-smc/ui-kit/src/components/Button";
import styles from "./catalog/layout.module.scss";
import { SideMenu } from "./components/sideMenu/SideMenu";
import { NavigateButton } from "../shared/components/sideBar/SideBarButtons";
import { SideMenuStoreProvider } from "./components/sideMenu/SideMenuStore";
import { useNavigate } from "react-router-dom";
import { Footer } from "./components/footer/Footer";
import classNames from "classnames";

export function NotFoundPage() {
    const navigate = useNavigate();

    return (
        <div className={page.wrapper}>
            <div className={styles.main}>
                <SideMenuStoreProvider>
                    <SideMenu>
                        <NavigateButton path={'/'} data-qa-btn-home>
                            <i className="sm sm-systemorph-fill"/>
                        </NavigateButton>
                    </SideMenu>
                </SideMenuStoreProvider>
                <div className={styles.content}>
                    <div className={page.pageNotFound}>
                        <div className={page.content}>
                            <h1 title="Page not found" className={page.title}>404</h1>
                            <div className={page.description}>
                                <p>You find yourself where no man has gone before.</p>
                                <p>Quick, beam us up, Scotty!</p>
                            </div>
                            <Button className={classNames(button.primaryButton, button.button)} label="Engage"
                                    onClick={() => navigate('/')}/>
                        </div>
                    </div>
                </div>
            </div>
            <Footer/>
        </div>
    );
}