import { useHeadings } from "./documentStore/hooks/useHeadings";
import styles from "./outline.module.scss";

export function TableOfContents() {
    const headings = useHeadings();

    const tableOfContents = headings
        .map(heading => {
            return (
                <li className={`${styles.level} level-${heading.rank}`} key={heading.id}>
                    <a className={styles.link} href={`#${heading.id}`}>{`${heading.number}. ${heading.text}`}</a>
                </li>
            );
        });

    return (
    <div>
        <div className={styles.outline}>
            <h1 className={styles.heading}>Outline</h1>
            <ul className={styles.list}>
                {tableOfContents}
            </ul>
        </div>
    </div>
    );
}