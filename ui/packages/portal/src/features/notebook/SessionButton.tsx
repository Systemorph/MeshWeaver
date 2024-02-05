import 'rc-dropdown/assets/index.css';
import { useStartSession } from "./documentStore/hooks/useStartSession";
import { useStopSession } from "./documentStore/hooks/useStopSession";
import classNames from "classnames";
import styles from "./header.module.scss";
import sessionButton from "./sessionButton.module.scss";
import { Button } from "@open-smc/ui-kit/components/Button";
import Dropdown from 'rc-dropdown';
import { useEffect, useState } from "react";
import { isEmpty, round } from "lodash";
import { SessionSpecification } from "../../app/notebookFormat";
import { SessionTierOverlay } from "./SessionTierOverlay";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { ImageSettingsDto, SessionTierSpecificationDto } from "../project/projectSessionApi";
import { rcTooltipOptions } from "../../shared/tooltipOptions";
import { useNotebookEditorSelector } from "./NotebookEditor";
import { useApi } from "../../ApiProvider";

export function SessionButton() {
    const {status: sessionStatus} = useNotebookEditorSelector("session");
    const evaluationStatus = useNotebookEditorSelector("evaluationStatus");
    const startSession = useStartSession();
    const stopSession = useStopSession();
    const notebook = useNotebookEditorSelector("notebook");
    const [specification, setSpecification] = useState<SessionSpecification>();
    const [availableTiers, setAvailableTiers] = useState<SessionTierSpecificationDto[]>();
    const [availableImages, setAvailableImages] = useState<ImageSettingsDto[]>();
    const projectId = useNotebookEditorSelector("projectId");
    const envId = useNotebookEditorSelector("envId");
    const {canRun} = useNotebookEditorSelector("permissions");
    const [overlayOpen, setOverlayOpen] = useState(false);
    const {ProjectSessionApi, EnvSessionApi} = useApi();

    useEffect(() => {
        (
            async () => {
                const tiers = await ProjectSessionApi.getTiers();
                const images = await ProjectSessionApi.getImages();
                setAvailableTiers(tiers);
                setAvailableImages(images);

                if (sessionStatus === "Running") {
                    setSpecification(notebook?.currentSession?.specification);
                } else if (!isEmpty(tiers)) {
                    const settings = await EnvSessionApi.getSessionSettings(projectId, envId, notebook.id);
                    const defaultTier = tiers[0];
                    const defaultImage = images[0];
                    const {cpu, memory, systemName: tier} = (settings.tier ?? defaultTier);
                    const {image, imageTag} = settings;
                    setSpecification({tier, cpu, memory, image: image ?? defaultImage.image, imageTag});
                }
            }
        )()
    }, [envId, projectId, notebook.id])

    const getText = () => {
        if (sessionStatus === 'Starting' || sessionStatus === 'Initializing') {
            return (
                <div>
                    <span className={classNames(styles.indicator, styles.startingIndicator)}/>
                    <span className={sessionButton.text} data-qa-status>Starting...</span>
                </div>
            );
        }

        if (sessionStatus === 'Stopping') {
            return (
                <div>
                    <span className={classNames(styles.indicator, styles.stoppingIndicator)}/>
                    <span className={sessionButton.text} data-qa-status>Stopping...</span>
                </div>
            );
        }

        if (sessionStatus === 'Running') {
            if (evaluationStatus === 'Idle') {
                return <span className={sessionButton.text} data-qa-status>Idle</span>;
            }

            if (evaluationStatus === 'Evaluating' || evaluationStatus === 'Pending') {
                return (
                    <div>
                        <span className={classNames(styles.indicator, styles.runningIndicator)}/>
                        <span className={sessionButton.text} data-qa-status>Running</span>
                    </div>
                );
            }
        }

        return null;
    }

    const sessionButtonClassNames = classNames(sessionButton.button, button.button, {
        started: sessionStatus === 'Running' || sessionStatus === 'Stopping',
        starting: sessionStatus === 'Starting',
        specificationEmpty: isEmpty(availableTiers)
    });

    return (
        <div className={sessionButton.box}>
            {getText()}
            <Button className={sessionButtonClassNames}
                    icon="sm sm-start-session"
                    type="button"
                    disabled={!canRun || sessionStatus === 'Stopping'}
                    onClick={sessionStatus === 'Stopped' ? () => startSession(specification) : stopSession}
                    tooltip={sessionStatus === 'Stopped' ? 'Start session' : ''}
                    tooltipOptions={rcTooltipOptions}
                    data-qa-btn-session
            >
                {!isEmpty(specification?.tier) && (
                    <div className={sessionButton.tierData}>
                        <span className={styles.tierProp}>CPU: {round(specification.cpu, 1)}</span>
                        <span className={styles.tierProp}>RAM: {round(specification.memory, 1)}</span>
                    </div>)}
            </Button>
            {!isEmpty(availableTiers) &&
                <Dropdown
                    trigger={['click']}
                    visible={overlayOpen}
                    onVisibleChange={open => setOverlayOpen(open)}
                    overlay={overlayOpen && <SessionTierOverlay
                        tiers={availableTiers}
                        images={availableImages}
                        model={specification}
                        onSelect={model => {
                            setSpecification(model);
                            startSession(model);
                        }}
                        onCancel={() => setOverlayOpen(false)}/>
                    }>
                    <Button className={sessionButton.chevron}
                            disabled={!canRun || sessionStatus !== "Stopped"}
                            data-qa-btn-session-params
                            type="button"
                            icon="sm sm-chevron-down"/>
                </Dropdown>
            }
        </div>
    );
}
