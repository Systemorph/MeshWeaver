import { MainWindow } from "./MainWindow";
import classNames from "classnames";
import styles from "./layoutStackControl.module.scss";
import { ModalWindow } from "./ModalWindow";
import { ControlView } from "../ControlDef";
import { RenderArea } from "../store/RenderArea";
import { LayoutStackSkin } from "@open-smc/layout/src/contract/controls/LayoutStackControl";

export interface LayoutStackView extends ControlView {
    areas?: string[];
    skin?: LayoutStackSkin;
    highlightNewAreas?: boolean;
    columnCount?: number;
}

export default function LayoutStackControl(props: LayoutStackView) {
    const {skin} = props;

    if (skin === "MainWindow") {
        return (
            <MainWindow {...props}/>
        );
    }

    if (skin === "Modal") {
        return (
            <ModalWindow {...props}/>
        );
    }

    return <LayoutStack {...props}/>;
}

function LayoutStack({id, skin, areas, style, highlightNewAreas, columnCount}: LayoutStackView) {
    const renderedAreas = areas?.map(id => {
        const className = classNames(
            styles.stackItem, {
                // isAdded: addedAreas.includes(area)
            }
        );

        return (
            <RenderArea id={id} className={className} key={id}/>
        );
    });

    const className = getStackClassNames(skin, highlightNewAreas);

    const cssVars = {
        ["--columnNumber"]: `${areas?.length}`,
        ["--columnCount"]: columnCount,
    };

    return (
        <div id={id} className={className} style={{...style, ...cssVars}}>
            {renderedAreas}
        </div>
    );
}

export function getStackClassNames(skin: LayoutStackSkin, highlightNewAreas: boolean) {
    return classNames(styles.container, {
        [`skin-${skin}`]: skin,
        highlightNewAreas
    });
}