import styles from "./modalWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import { AreaChangedEvent } from "../contract/application.contract";
import classNames from "classnames";
import { useMessageHub } from "../AddHub";
import { Area } from "../Area";

export const modalWindowAreas = {
    main: "Main",
    header: "Header",
    footer: "Footer",
} as const;

export type ModalWindowArea = typeof modalWindowAreas[keyof typeof modalWindowAreas];

export function ModalWindow({id, areas, style}: StackView) {
    const areasByKey = keyBy(areas, "area") as Record<ModalWindowArea, AreaChangedEvent>;

    const main = areasByKey[modalWindowAreas.main];
    const title = areasByKey[modalWindowAreas.header];
    const footer = areasByKey[modalWindowAreas.footer];

    const hub = useMessageHub();

    const className = classNames(styles.modalWindow);

    return (
        <div id={id} className={className} style={style}>
            {title &&
                <Area
                    hub={hub}
                    event={title}
                    render={
                        view =>
                            <div className={styles.header}>
                                {view}
                            </div>
                    }
                />
            }
            {main &&
                <Area
                    hub={hub}
                    event={main}
                    render={
                        view =>
                            <div className={styles.main}>
                                {view}
                            </div>
                    }
                />
            }
            {footer &&
                <Area
                    hub={hub}
                    event={footer}
                    render={
                        view =>
                            <div className={styles.footer}>
                                {view}
                            </div>
                    }
                />
            }
        </div>
    );
}

