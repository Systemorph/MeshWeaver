import styles from "./modalWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import classNames from "classnames";
import { basename } from "path-browserify";
import { RenderArea } from "../app/RenderArea";

export const modalWindowAreas = {
    main: "Main",
    header: "Header",
    footer: "Footer",
} as const;

export type ModalWindowArea = typeof modalWindowAreas[keyof typeof modalWindowAreas];

export function ModalWindow({areas, style}: StackView) {
    const mappedAreas = keyBy(areas, basename);

    const mainAreaId = mappedAreas[modalWindowAreas.main];
    const titleAreaId = mappedAreas[modalWindowAreas.header];
    const footerAreaId = mappedAreas[modalWindowAreas.footer];

    const className = classNames(styles.modalWindow);

    return (
        <div className={className} style={style}>
            {titleAreaId &&
                <RenderArea
                    id={titleAreaId}
                    render={
                        view =>
                            <div className={styles.header}>
                                {view}
                            </div>
                    }
                />
            }
            {mainAreaId &&
                <RenderArea
                    id={mainAreaId}
                    render={
                        view =>
                            <div className={styles.main}>
                                {view}
                            </div>
                    }
                />
            }
            {footerAreaId &&
                <RenderArea
                    id={footerAreaId}
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

