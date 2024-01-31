import styles from "./sessionTier.module.scss";
import { Button } from "@open-smc/ui-kit/components/Button";
import '../../shared/components/slider.scss';
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import * as React from "react";
import { useRef, useState } from "react";
import { SessionSpecification } from "../../app/notebookFormat";
import classNames from "classnames";
import { ImageSettingsDto, SessionTierSpecificationDto } from "../project/projectSessionApi";
import { TierField } from "../sessionSettings/TierField";
import { SubmitHandler, useForm, useWatch } from "react-hook-form";
import { CpuField } from "../sessionSettings/CpuField";
import { MemoryField } from "../sessionSettings/MemoryField";
import { CreditsPerMinuteField } from "../sessionSettings/CreditsPerMinuteField";
import { ImageField } from "../sessionSettings/ImageField";
import { ImageTagField } from "../sessionSettings/ImageTagField";

export interface SessionButtonFormModel {
    image: string;
    imageTag: string;
    tier: string;
    cpu: number;
    memory: number;
}

type OverlayProps = {
    images: ImageSettingsDto[];
    tiers: SessionTierSpecificationDto[];
    model: SessionSpecification;
    onSelect: (specification: SessionSpecification) => void;
    onCancel: () => void;
};

export function SessionTierOverlay({images, tiers, model, onSelect, onCancel}: OverlayProps) {
    const ref = useRef();

    const [includePrereleaseTags, setIncludePrereleaseTags] = useState(false);

    const form = useForm({
        mode: 'all',
        defaultValues: model
    });

    const {handleSubmit} = form;

    const submit: SubmitHandler<SessionSpecification> = (model, event) => {
        event.preventDefault();
        onSelect(model);
        onCancel();
    };

    return (
        <div ref={ref} className={classNames(styles.dropdownOverlay, "session-tier")} data-qa-dialog-session-params>
            <form onSubmit={handleSubmit(submit)} autoComplete="off">
                <div className={styles.wrapper}>
                    <h1 className={styles.header}>Set session parameters</h1>
                    <ImageField
                        images={images}
                        includePrereleaseTags={includePrereleaseTags}
                        setIncludePrereleaseTags={setIncludePrereleaseTags}
                        form={form as any}
                        getPopupContainer={() => ref.current}/>
                    <ImageTagField
                        images={images}
                        includePrereleaseTags={includePrereleaseTags}
                        onIncludePrereleaseTagsChange={setIncludePrereleaseTags}
                        form={form as any}
                        getPopupContainer={() => ref.current}/>
                    <TierField
                        tiers={tiers}
                        form={form as any}
                        getPopupContainer={() => ref.current}
                    />
                    <CpuField tiers={tiers} form={form as any}/>
                    <MemoryField tiers={tiers} form={form as any}/>
                    <CreditsPerMinuteField tiers={tiers} form={form as any}/>
                </div>

                <div className={styles.buttonsContainer}>
                    <Button className={classNames(button.primaryButton, styles.apply, button.button)}
                            icon="sm sm-check"
                            label="Start"
                            data-qa-btn-start
                            type={'submit'}
                    />
                    <Button className={classNames(button.cancelButton, styles.cancel, button.button)}
                            data-qa-btn-cancel
                            type="button"
                            icon="sm sm-close"
                            label="Cancel"
                            onClick={onCancel}
                    />
                </div>
            </form>
        </div>)
}