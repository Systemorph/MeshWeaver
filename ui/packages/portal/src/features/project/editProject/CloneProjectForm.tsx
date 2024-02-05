import styles from './setupProject.module.scss';
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";
import { useForm } from 'react-hook-form';
import { BaseSyntheticEvent, useEffect, useState } from "react";
import { isEmpty } from "lodash";
import { Project, ProjectApi, ProjectSettings } from "../../../app/projectApi";
import { NameField } from "./NameField";
import { DescriptionField } from "./DescriptionField";
import { HomeRegionFieldReadOnly } from "./HomeRegionFieldReadOnly";
import { IdField } from "./IdField";
import { EnvironmentField } from "./EnvironmentField";
import { FormHeader } from "../../../shared/components/sideMenuComponents/FormHeader";
import { IdEditorField } from "./IdEditorField";
import { useSuggestId } from "./useSuggestId";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { useToast } from "@open-smc/application/src/notifications/useToast";
import classNames from "classnames";

type Props = {
    projectId: string,
    environment?: string,
    onCancel?: () => void,
    onFinish?: (newProjectId: string) => void,
    onClose?: () => void
};

export function CloneProjectForm({projectId, environment, onCancel, onFinish, onClose}: Props) {
    const [project, setProject] = useState<Project>();
    const [newId, setNewId] = useState<string>();

    useEffect(() => {
        (async function () {
            const project = await ProjectApi.getProject(projectId);
            const newId = await ProjectApi.suggestId(project.name);
            setProject(project);
            setNewId(newId);
        })();
    }, [projectId]);

    if (!project || !newId) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return <CloneProjectFormInner project={project}
                                  newId={newId}
                                  environment={environment}
                                  onCancel={onCancel}
                                  onFinish={onFinish}
                                  onClose={onClose}/>;
}

type InnerProps = {
    project: Project;
    newId: string;
    environment?: string,
    onCancel: () => void,
    onFinish: (newProjectId: string) => void,
    onClose: () => void
};

function CloneProjectFormInner({project, newId, environment, onCancel, onFinish, onClose}: InnerProps) {
    const {showToast} = useToast();
    const form = useForm<ProjectSettings>({
        mode: 'onChange',
        defaultValues: {
            id: newId,
            name: project.name,
            abstract: project.abstract,
            thumbnail: project.thumbnail,
            homeRegion: project.homeRegion,
            environment: environment || project.defaultEnvironment,
        }
    });
    const {handleSubmit, formState: {errors, isValidating}} = form;
    const [isIdEditable, setIsIdEditable] = useState(false);
    const [isSubmitted, setIsSubmitted] = useState(false);
    const {suggestId, isLoading: suggestIsLoading} = useSuggestId(form, !isIdEditable);

    const submit = (data: ProjectSettings, event: BaseSyntheticEvent) => {
        event.preventDefault();

        setIsSubmitted(true);

        return ProjectApi.cloneProject(project.id, data)
            .then(() => onFinish(data.id))
            .catch(error => {
                showToast('Error', 'Failed to clone project', 'Error');
                setIsSubmitted(false);
                throw error;
            });
    }

    const disabled = !isEmpty(errors) || isValidating || suggestIsLoading || isSubmitted;

    return (
        <div data-qa-form-project-clone>
            <FormHeader onClose={onClose} text={'Clone project'}/>

            <form onSubmit={handleSubmit(submit)} className={styles.form} autoComplete="off">
                <NameField form={form}/>
                {isIdEditable
                    ? <IdEditorField form={form}
                                     onRefresh={suggestId}
                                     isLoading={suggestIsLoading}/>
                    : <IdField form={form}
                               onEdit={() => setIsIdEditable(true)}
                               isLoading={suggestIsLoading}/>
                }
                <EnvironmentField form={form} environments={project.environments}/>
                <DescriptionField form={form}/>
                <HomeRegionFieldReadOnly form={form}/>
                <div className={styles.container}>
                    <Button className={classNames(button.button, button.primaryButton)}
                            type="submit"
                            icon="sm sm-copy"
                            label="Clone"
                            disabled={disabled}
                            data-qa-btn-create/>
                    <Button className={classNames(button.cancelButton, button.button)}
                            type="button"
                            icon="sm sm-close"
                            label="Cancel"
                            onClick={() => onCancel()}
                            data-qa-btn-cancel/>
                </div>
            </form>
        </div>
    );
}