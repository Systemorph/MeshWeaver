import React from "react";
import { Line } from "rc-progress";
import styles from "./progressControl.module.scss";
import { ControlView } from "../ControlDef";

export interface ProgressView extends ControlView {
    progress: number;
    message: string;
}

export default function ProgressControl({id, progress, message}: ProgressView) {
    return (
        <>
            <div id={id} className={styles.container}>
                <div className={styles.message}>
                    {message && <span>{`${message} –`}</span>}
                    <span className={styles.progress}> {`${progress}%`}</span>
                </div>
                <Line percent={progress} strokeWidth={1} trailColor="transparent" strokeLinecap={"round"}/>
            </div>
        </>
    );
}
