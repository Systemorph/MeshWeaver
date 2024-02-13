import { useEffect, useState } from "react";
import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import styles from "./modalWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import { AreaChangedEvent } from "../contract/application.contract";
import classNames from "classnames";

export const modalWindowAreas = {
    main: "Main",
    header: "Header",
    footer: "Footer",
} as const;

export type ModalWindowArea = typeof modalWindowAreas[keyof typeof modalWindowAreas];

export function ModalWindow({id, areas, style}: StackView) {
    const areasByKey = keyBy(areas, "area") as Record<ModalWindowArea, AreaChangedEvent>;

    const [main, setMain] = useState(areasByKey[modalWindowAreas.main]);
    const [title, setTitle] = useState(areasByKey[modalWindowAreas.header]);
    const [footer, setFooter] = useState(areasByKey[modalWindowAreas.footer]);

    useSubscribeToAreaChanged(setMain, modalWindowAreas.main);
    useSubscribeToAreaChanged(setTitle, modalWindowAreas.header);
    useSubscribeToAreaChanged(setFooter, modalWindowAreas.footer);

    useEffect(() => {
        const areasByKey = keyBy(areas, "area") as Record<ModalWindowArea, AreaChangedEvent>;
        setMain(areasByKey[modalWindowAreas.main]);
        setTitle(areasByKey[modalWindowAreas.header]);
        setFooter(areasByKey[modalWindowAreas.footer]);
    }, [areas]);

    const className = classNames(styles.modalWindow);

    return (
        <div id={id} className={className} style={style}>
            {title?.view &&
                <div className={styles.header}>
                    {renderControl(title.view)}
                </div>
            }
            {main?.view &&
                <div className={styles.main}>
                    {renderControl(main.view)}
                </div>
            }
            {footer?.view &&
                <div className={styles.footer}>
                    {renderControl(footer.view)}
                </div>
            }
        </div>
    );
}

