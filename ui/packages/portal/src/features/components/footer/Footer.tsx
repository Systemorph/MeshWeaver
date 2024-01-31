import styles from './footer.module.scss';
import { PropsWithChildren } from "react";


// TODO uncomment it when functionality will be available. Requested by Ksenia R. (30.08.2022, aberezutsky)
export function Footer({children}: PropsWithChildren<{}>) {
    return (
        <div className={styles.footer} data-qa-footer>

            {/*<ul className={styles.controls}>*/}
            {/*    <li className={styles.control}>*/}
            {/*        <i className="sm sm-maximize"/>*/}
            {/*    </li>*/}
            {/*    <li className={styles.control}>*/}
            {/*        <i className="sm sm-code"/>*/}
            {/*    </li>*/}

            {/*</ul>*/}
            <ul className={styles.menu}>
                {children}
            </ul>
        </div>
    );
}
