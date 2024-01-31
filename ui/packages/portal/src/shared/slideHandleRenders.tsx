import { SliderProps } from "rc-slider";
import { HandleTooltip } from "./components/HandleTooltip";
import * as React from "react";
import styles from "../features/notebook/sessionTier.module.scss";
import { round } from "lodash";

export const tooltipHandleRender: SliderProps['handleRender'] = (node, {value, dragging}) => {
    return (
        <HandleTooltip value={value} visible={dragging}>
            {node}
        </HandleTooltip>
    );
};

export const labelHandleRender: SliderProps['handleRender'] = (node, handleProps) => {
    return (
        <div>
            {node}
            <label className={styles.sliderHandler} style={{...node.props.style}}>
                {round(handleProps.value,1)}
            </label>
        </div>
    );
};