import { HTMLAttributes } from "react";
import { isEmpty } from "lodash";
import styles from "./file-list.module.scss";
import { usePath } from "./projectExplorerStore/hooks/usePath";
import { useGoTo } from "./projectExplorerStore/hooks/useGoTo";
import { Path } from "../../../shared/utils/path";

export function Breadcrumbs() {
    const path = usePath();
    const goTo = useGoTo();
    const renderedItems = path
        ? Path.split(path).map((part, index, parts) => {
            const isLast = index === parts.length - 1;
            const path = Path.join(...parts.slice(0, index + 1));
            const onClick = !isLast ? () => goTo(path) : null;
            return <Breadcrumb className={styles.breadcrumb} onClick={onClick} key={path}
                               data-qa-breadcrumb={part}>{part}</Breadcrumb>;
        })
        : [];

    const rootItem = (
        <Breadcrumb className={styles.rootBreadcrumb} onClick={isEmpty(renderedItems) ? null : () => goTo()}
                    key={'root'} data-qa-breadcrumb>
            <i className="sm sm-folder"/>
        </Breadcrumb>
    );

    renderedItems.splice(0, 0, rootItem);

    const itemsCount = renderedItems.length;

    return (
        <div className={styles.breadcrumbs} data-qa-breadcrumbs>
            {renderedItems.map((item, index) =>
                itemsCount > 1 && index === itemsCount - 1
                    ? [item]
                    : [item, <span className={styles.separator} key={'separator' + index}>/</span>])}
        </div>
    );
}

function Breadcrumb({className, onClick, children, ...props}: HTMLAttributes<HTMLSpanElement>) {
    return (
        <span className={`${className} ${!onClick ? 'active' : ''}`} onClick={onClick} {...props}>
            {children}
        </span>
    );
}
