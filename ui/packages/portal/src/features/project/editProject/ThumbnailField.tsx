import styles from './setupProject.module.scss';
import { Controller, UseFormReturn } from 'react-hook-form';
import { defaultThumbnail, ProjectSettings } from "../../../app/projectApi";
import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import { useState } from 'react';
import { isEmpty } from 'lodash';
import classNames from "classnames";

type Props = {
    form: UseFormReturn<ProjectSettings>;
    disabled?: boolean;
};

class Deferred<T> {
    public readonly promise: Promise<T>;
    public resolve: (val?: T) => void;
    public reject: (reason?: string) => void;

    constructor() {
        this.promise = new Promise<T>((resolve, reject) => {
            this.resolve = resolve;
            this.reject = reject;
        })
    }
}

export function ThumbnailField({form, disabled}: Props) {
    const {control, formState, setError} = form;
    const {errors, dirtyFields} = formState;
    const [isValidating, setIsValidating] = useState(false);
    const [imageValidity] = useState({} as Record<string, Deferred<boolean>>);

    return (
        <div className={styles.field} data-qa-field-thumbnail>
            <label className={styles.label} htmlFor="thumbnail">Thumbnail Image</label>
            <Controller name="thumbnail"
                        control={control}
                        rules={{
                            validate: async (url) => {
                                if (isEmpty(url))
                                    return true;

                                setError('thumbnail', {type: "loading"});
                                setIsValidating(true);

                                try {
                                    const ret = await imageValidity[url].promise;

                                    return ret;
                                } catch (e) {
                                    return e as string;
                                } finally {
                                    delete imageValidity[url];
                                    setIsValidating(false);
                                }
                            }
                        }}
                        render={({field}) => {
                            return <>
                                <span className={classNames("p-input-icon-right", styles.thumbnail, {disabled})}>
                                    {
                                        !isValidating &&
                                        <i className="pi sm sm-close"
                                           onClick={(e) => {
                                               e.preventDefault();
                                               form.setValue("thumbnail", undefined, {
                                                   shouldDirty: true,
                                                   shouldTouch: true,
                                                   shouldValidate: true
                                               });
                                           }}/>
                                    }
                                    <InputText
                                        className={styles.hasControl}
                                        id={field.name}
                                        {...field}
                                        onChange={(e) => {
                                            imageValidity[e.currentTarget.value] = new Deferred<boolean>();
                                            field.onChange(e);
                                        }}
                                        disabled={disabled}
                                        processing={isValidating}
                                        invalid={!isValidating && !!errors.thumbnail}/>
                                </span>
                                {
                                    <div
                                        className={classNames(styles.thumbnailPreview, isValidating || errors.thumbnail ? styles.thumbnailPreviewValidating : '')}>
                                        <img src={isEmpty(field.value?.trim()) ? defaultThumbnail : field.value}
                                             alt={form.getValues("name")}
                                             onLoad={() => {
                                                 imageValidity[field.value]?.resolve(true);
                                             }}
                                             onError={() => {
                                                 imageValidity[field.value]?.reject("Invalid image url");
                                             }}/>
                                    </div>
                                }
                            </>
                        }}/>
            {
                dirtyFields.thumbnail &&
                !isValidating &&
                !!errors.thumbnail &&
                <small className={styles.errorMessage}>{errors.thumbnail.message}</small>
            }
            <span className={styles.sizeLabel}>Recommended size: 578px &#215; 284px</span>
        </div>
    );
}