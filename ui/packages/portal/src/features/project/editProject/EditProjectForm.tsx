import styles from './setupProject.module.scss';
import { useForm } from 'react-hook-form';
import { Button } from "@open-smc/ui-kit/components/Button";
import classNames from "classnames";
import React, { useCallback, useEffect } from "react";
import { isEmpty } from "lodash";
import { useProject } from "../projectStore/hooks/useProject";
import { ProjectApi, ProjectSettings } from "../../../app/projectApi";
import { NameField } from "./NameField";
import { DescriptionField } from "./DescriptionField";
import { HomeRegionFieldReadOnly } from "./HomeRegionFieldReadOnly";
import { IdField } from "./IdField";
import { ThumbnailField } from './ThumbnailField';
import button from "@open-smc/ui-kit/components/buttons.module.scss"

type Props = {
    canEdit: boolean;
    onUpdated: () => void;
}

export function EditProjectForm({canEdit, onUpdated}: Props) {
    const {project} = useProject();

    const fieldValues = {
        id: project.id,
        name: project.name,
        abstract: project.abstract,
        defaultEnvironment: project.defaultEnvironment,
        thumbnail: project.thumbnail || "",
        homeRegion: project.homeRegion
    };

    const form = useForm<ProjectSettings>({
        mode: 'onChange',
        defaultValues: fieldValues
    });

    const {reset, handleSubmit, formState: {errors, isDirty, isSubmitted}} = form;

    useEffect(() => reset(fieldValues), [project]);

    const submit = useCallback((data: ProjectSettings) => {
        return ProjectApi.updateProject(project.id, {...data, defaultEnvironment: project.defaultEnvironment})
            .then(() =>
                ProjectApi.getProject(project.id)
                    .then(() => {
                        onUpdated();
                    }));
    }, [onUpdated, project]);

    const disabled = !canEdit || !isEmpty(errors) || !isDirty || isSubmitted;

    return (
        <div className={styles.formContainer} data-qa-form-project-settings>
            <form onSubmit={handleSubmit(submit)} className={styles.form} autoComplete="off">
                <NameField form={form} disabled={!canEdit}/>
                <IdField form={form}/>
                <DescriptionField form={form} disabled={!canEdit}/>
                <HomeRegionFieldReadOnly form={form}/>
                <ThumbnailField form={form} disabled={!canEdit}/>
                <Button disabled={disabled}
                        type="submit"
                        label="save"
                        className={classNames(styles.create, button.button, disabled ? 'disabled' : '')}
                        data-qa-btn-save/>
            </form>
        </div>
    );
}