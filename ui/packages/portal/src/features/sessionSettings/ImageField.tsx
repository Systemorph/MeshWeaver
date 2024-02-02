import { ImageSettingsDto } from "../project/projectSessionApi";
import styles from "../project/editProject/setupProject.module.scss";
import { Controller } from "react-hook-form";
import { Select } from "@open-smc/ui-kit/src/components/Select";
import * as React from "react";
import { FormFieldProps } from "./SessionSettingsForm";
import classNames from "classnames";
import sessionSettings from "./session-settings.module.scss";
import Tooltip from "rc-tooltip";
import { find, first } from "lodash";
import { BaseSelectProps } from "rc-select/lib/BaseSelect";

export interface ImageFormModel {
    readonly image: string;
    readonly includePrereleaseTags: boolean;
    readonly imageTag: string;
}

interface ImageFieldProps extends FormFieldProps<ImageFormModel> {
    images: ImageSettingsDto[];
    includePrereleaseTags: boolean;
    setIncludePrereleaseTags: (value: boolean) => void;
    tooltip?: boolean;
}

export function ImageField({
                               form: {control, setValue, getValues, resetField},
                               images,
                               includePrereleaseTags,
                               setIncludePrereleaseTags,
                               disabled,
                               onChange,
                               tooltip,
                               getPopupContainer
                           }: ImageFieldProps & Pick<BaseSelectProps, 'getPopupContainer'>) {
    const className = classNames(styles.innerContainer, {'single-value': images.length === 1});

    return (
        <div className={classNames(styles.field, sessionSettings.field)} data-qa-image>
            <label className={styles.label} htmlFor="image">Kernel image</label>
            <div className={className}>
                <Controller name="image"
                            control={control}
                            render={({field}) => {
                                const image = find(images, t => t.image === field.value);

                                if (images.length === 1) {
                                    return (
                                        <label className={styles.tierName}>{images[0].displayName}</label>
                                    );
                                }

                                return (
                                    <Select
                                        value={image}
                                        options={images}
                                        keyBinding={'image'}
                                        nameBinding={'displayName'}
                                        disabled={disabled}
                                        onSelect={image => {
                                            field.onChange(image.image);
                                            setIncludePrereleaseTags(false);
                                            const imageTag = first(image.imageTags?.filter(t => !t.isPreRelease))?.imageTag;
                                            setValue('imageTag', imageTag);
                                            onChange && onChange();
                                        }}
                                        getPopupContainer={getPopupContainer}
                                    />
                                );
                            }}/>

                {tooltip &&
                    <Tooltip overlayClassName={"tooltip"} placement={'right'}
                             overlay={<span>Version of Kernel operating<br/>system used for the session.</span>}>
                        <i className={classNames(sessionSettings.fieldInfo, "sm sm-info")}/>
                    </Tooltip>
                }
            </div>
        </div>
    );
}