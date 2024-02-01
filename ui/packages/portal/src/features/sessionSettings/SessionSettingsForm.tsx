import styles from "../project/editProject/setupProject.module.scss";
import settings from "./session-settings.module.scss";
import {
    ImageSettingsDto,
    SessionSettingsDto,
    SessionTierSpecificationDto
} from "../project/projectSessionApi";
import * as React from "react";
import { useCallback, useEffect, useRef, useState } from "react";
import { debounce, find } from "lodash";
import { useForm, UseFormReturn } from "react-hook-form";
import { IdleTimeout } from "./IdleTimeout";
import { TierField } from "./TierField";
import { CpuField } from "./CpuField";
import { MemoryField } from "./MemoryField";
import { Button } from "@open-smc/ui-kit/components/Button";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import classNames from "classnames";
import { CreditsPerMinuteField } from "./CreditsPerMinuteField";
import { ImageField } from "./ImageField";
import { ImageTagField } from "./ImageTagField";

interface SessionSettingProps {
    model: SessionSettingsFormModel;
    tiers: SessionTierSpecificationDto[];
    images: ImageSettingsDto[];
    editable: boolean;
    onUpdate: (model: Partial<SessionSettingsFormModel>) => void;
    canInherit?: boolean;
    onRestoreInheritance?: () => void;
    onOverride?: () => void;
}

export interface SessionSettingsFormModel {
    image: string;
    imageTag: string;
    tier: string;
    cpu: number;
    memory: number;
    sessionIdleTimeout: number;
    applicationIdleTimeout: number;
    inherited?: boolean;
}

export function getModel(settings: SessionSettingsDto) {
    const {image, imageTag, tier, sessionIdleTimeout, applicationIdleTimeout, inherited} = settings;

    return {
        image,
        imageTag,
        tier: tier?.systemName,
        cpu: tier?.cpu,
        memory: tier?.memory,
        sessionIdleTimeout,
        applicationIdleTimeout,
        inherited
    };
}

export function SessionSettingsForm({
                                        model,
                                        tiers,
                                        images,
                                        editable,
                                        canInherit,
                                        onUpdate,
                                        onRestoreInheritance,
                                        onOverride
                                    }: SessionSettingProps) {
    const ref = useRef();

    const {tier, inherited} = model;

    const [includePrereleaseTags, setIncludePrereleaseTags] = useState(false);

    const form = useForm({
        mode: 'all',
        defaultValues: model
    });

    const {reset} = form;

    useEffect(() => {
        reset(model);
    }, [model]);

    const disabled = !editable || (canInherit && inherited);

    const onCpuOrMemoryChange = () => {
        const {cpu, memory} = form.getValues();
        onUpdate({cpu, memory});
    }

    const onTimeoutChange = useCallback(debounce((fieldName: 'sessionIdleTimeout' | 'applicationIdleTimeout') => {
        if (!form.getFieldState(fieldName).error) {
            onUpdate({[fieldName]: form.getValues()[fieldName]});
        }
    }, 500), [onUpdate]);

    return (
        <div ref={ref} className={styles.formContainer} data-qa-form-session-settings>
            <form className={classNames(styles.form, {inherited})} autoComplete="off">
                <ImageField
                    images={images}
                    includePrereleaseTags={includePrereleaseTags}
                    setIncludePrereleaseTags={setIncludePrereleaseTags}
                    form={form as any}
                    disabled={disabled}
                    onChange={() => {
                        const {image, imageTag} = form.getValues();
                        onUpdate({image, imageTag});
                    }}
                    tooltip={true}
                    getPopupContainer={() => ref.current}
                />
                <ImageTagField
                    images={images}
                    includePrereleaseTags={includePrereleaseTags}
                    form={form as any}
                    disabled={disabled}
                    onChange={() => {
                        const {imageTag} = form.getValues();
                        onUpdate({imageTag});
                    }}
                    onIncludePrereleaseTagsChange={setIncludePrereleaseTags}
                    tooltip={true}
                    getPopupContainer={() => ref.current}
                />
                <TierField
                    tiers={tiers}
                    form={form as any}
                    disabled={disabled}
                    onChange={() => {
                        const {tier, cpu, memory} = form.getValues();
                        onUpdate({tier, cpu, memory});
                    }}
                    tooltip={true}
                    getPopupContainer={() => ref.current}
                />
                {tier &&
                    <>
                        <CpuField
                            tiers={tiers}
                            form={form as any}
                            disabled={disabled}
                            onChange={onCpuOrMemoryChange}
                            tooltip={true}
                        />
                        <MemoryField
                            tiers={tiers}
                            form={form as any}
                            disabled={disabled}
                            onChange={onCpuOrMemoryChange}
                        />
                        <CreditsPerMinuteField tiers={tiers} form={form as any}/>
                    </>
                }
                <div className={settings.timeoutsContainer}>
                    <IdleTimeout
                        form={form as any}
                        name={'sessionIdleTimeout'}
                        description={'Session idle timeout'}
                        tooltip={<span>Session timeout after which the session will be stopped <br/>in case of no user activity.</span>}
                        disabled={disabled}
                        onChange={() => onTimeoutChange('sessionIdleTimeout')}
                        onBlur={() => onTimeoutChange.flush()}
                        onKeydown={({key}) => key === 'Enter' && onTimeoutChange.flush()}
                    />
                    <IdleTimeout
                        form={form as any}
                        name={'applicationIdleTimeout'}
                        description={'Application idle timeout'}
                        tooltip={<span>Session timeout after which the<br/>session will be stopped<br/>in case of the closed browser window.</span>}
                        disabled={disabled}
                        onChange={() => onTimeoutChange('applicationIdleTimeout')}
                        onBlur={() => onTimeoutChange.flush()}
                        onKeydown={({key}) => key === 'Enter' && onTimeoutChange.flush()}
                    />
                </div>

            </form>
            {editable && canInherit && inherited &&
                <Button onClick={() => onOverride()}
                        className={classNames(button.primaryButton, button.button, settings.button)}
                        label={'Override'}
                        icon="sm sm-edit"/>
            }
            {editable && canInherit && !inherited &&
                <Button className={classNames(button.secondaryButton, button.button, settings.restoreButton)}
                        icon="sm sm-undo"
                        onClick={() => onRestoreInheritance()} label={'Restore'}/>
            }
        </div>
    );
}

export function getImageTags(images: ImageSettingsDto[], image: string) {
    return find(images, i => i.image === image).imageTags;
}

export interface FormFieldProps<TForm> {
    form: UseFormReturn<TForm>;
    disabled?: boolean;
    onChange?: () => void;
    onBlur?: () => void;
    onKeydown?: (event: React.KeyboardEvent<HTMLInputElement>) => void;
}