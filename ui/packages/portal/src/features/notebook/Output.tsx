import React, { Suspense } from "react";
import classNames from "classnames";
import styles from "./element.module.scss";
import { ErrorBoundary } from "@open-smc/ui-kit/src/components/ErrorBoundary";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { Area } from "@open-smc/application/src/Area";

const errorFallback = <div style={{color: 'red'}}>Failed to render output</div>;
const suspenseFallback = <div>Loading...</div>;

interface OutputProps {
    event: AreaChangedEvent;
}

export function Output({event}: OutputProps) {
    return (
        <Area event={event} render={view => (
            <div className={classNames('output'/*, `output-${presenter.name}`*/)} data-qa-output>
                <div className={styles.innerContainer}>
                    <ErrorBoundary fallback={(error) => errorFallback}>
                        <Suspense fallback={suspenseFallback}>
                            {view}
                        </Suspense>
                    </ErrorBoundary>
                </div>
            </div>
        )}/>
    );
}