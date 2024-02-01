import '@open-smc/application/SmappApp.scss';
import { useSmappSelector } from "./useSmappSelector";
import button from "@open-smc/ui-kit/components/buttons.module.scss";
import { ErrorBoundary } from "@open-smc/ui-kit/components/ErrorBoundary";
import { Suspense } from 'react';
import { getPresenter } from "@open-smc/rendering";
import { Button } from "@open-smc/ui-kit/components/Button";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function Smapp() {
    const {smappStatus, data: presenter} = useSmappSelector('smappStatusEvent');

    switch (smappStatus) {
        case 'Initializing':
            return <div className={loader.loading}>Initializing...</div>;
        case 'Stopped':
            return <div>Application stopped</div>;
        case 'Failed':
            return <div className="loading">Application failed</div>;
        case 'Ready':
            return (
                <ErrorBoundary
                    fallback={(error, reset) => <div className="smappError"><p>{error as string}</p> <Button
                        className={button.primaryButton} onClick={reset} label={'Retry'}/></div>}>
                    <Suspense fallback={<div className="loading">Loading...</div>}>
                        {getPresenter(presenter)}
                    </Suspense>
                </ErrorBoundary>
            );
    }
}