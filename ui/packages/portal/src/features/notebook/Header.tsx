import { Toolbar } from "./Toolbar";
import styles from "./header.module.scss";

export function Header() {
    return (
        <div className={`${styles.header} header-indent`} data-qa-header>
            <Toolbar/>
        </div>
    );
}