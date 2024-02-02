import { ImageSettingsDto } from "../project/projectSessionApi";
import styles from "../project/editProject/setupProject.module.scss";
import { Controller, useWatch } from "react-hook-form";
import { Select } from "@open-smc/ui-kit/src/components/Select";
import * as React from "react";
import { FormFieldProps } from "./SessionSettingsForm";
import classNames from "classnames";
import sessionSettings from "./session-settings.module.scss";
import Tooltip from "rc-tooltip";
import { filter, find } from "lodash";
import { BaseSelectProps } from "rc-select/lib/BaseSelect";
import Switch from "rc-switch";

export interface ImageFormModel {
    readonly image: string;
    readonly includePrereleaseTags: boolean;
    readonly imageTag: string;
}

interface ImageTagFieldProps extends FormFieldProps<ImageFormModel> {
    images: ImageSettingsDto[];
    includePrereleaseTags: boolean;
    onIncludePrereleaseTagsChange: (value: boolean) => void;
    tooltip?: boolean;
}

export function ImageTagField({
                                  form: {control, setValue, getValues},
                                  images,
                                  includePrereleaseTags,
                                  onIncludePrereleaseTagsChange,
                                  disabled,
                                  onChange,
                                  tooltip,
                                  getPopupContainer
                              }: ImageTagFieldProps & Pick<BaseSelectProps, 'getPopupContainer'>) {
    const image = useWatch({
        control,
        name: 'image'
    });

    const imageObj = find(images, t => t.image === image);
    const hasPrereleaseTags = filter(imageObj?.imageTags, t => t.isPreRelease).length > 0;
    const imageTags = imageObj?.imageTags?.filter(t => !t.isPreRelease || includePrereleaseTags);

    const singleValue = imageTags?.length === 1;

    return (
        <div className={classNames(styles.field, sessionSettings.field, sessionSettings.kernelField)} data-qa-image-tag>
            <label className={styles.label} htmlFor="imageTag">Kernel image version</label>
            <div className={styles.innerContainer}>
                <Controller name="imageTag"
                            control={control}
                            render={({field}) => {
                                const imageTag = find(imageTags, t => t.imageTag === field.value);

                                return (
                                    <>
                                        <Select
                                            value={imageTag}
                                            options={imageTags}
                                            keyBinding={'imageTag'}
                                            nameBinding={'imageTag'}
                                            disabled={disabled || singleValue}
                                            onSelect={imageTag => {
                                                field.onChange(imageTag.imageTag);
                                                onChange && onChange();
                                            }}
                                            getPopupContainer={getPopupContainer}
                                        />
                                        <div className={sessionSettings.switchContainer}>
                                            <Switch checked={includePrereleaseTags}
                                                    onChange={onIncludePrereleaseTagsChange}
                                                    disabled={disabled || !hasPrereleaseTags}
                                                    id={'includePrereleases'}
                                            />
                                            <label htmlFor={'includePrereleases'}
                                                   className={classNames(sessionSettings.switchLabel)}>Show
                                                pre-releases</label>
                                        </div>
                                    </>
                                );
                            }}/>

                {tooltip &&
                    <Tooltip overlayClassName={"tooltip"} placement={'right'}
                             overlay={<span>Version of the image used for the session.</span>}>
                        <i className={classNames(sessionSettings.fieldInfo, "sm sm-info")}/>
                    </Tooltip>
                }
            </div>
        </div>
    );
}