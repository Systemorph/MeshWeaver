import styles from './setupProject.module.scss';
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { useForm } from 'react-hook-form';
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { BaseSyntheticEvent, useCallback, useEffect, useState } from "react";
import { isEmpty } from "lodash";
import { Project, ProjectApi, ProjectSettings } from "../../../app/projectApi";
import { IdField } from "./IdField";
import { NameField } from "./NameField";
import { DescriptionField } from "./DescriptionField";
import { HomeRegionField } from "./HomeRegionField";
import { DefaultEnvField } from "./DefaultEnvField";
import { FormHeader } from "../../../shared/components/sideMenuComponents/FormHeader";
import { IdEditorField } from "./IdEditorField";
import { useSuggestId } from "./useSuggestId";
import { useToast } from "@open-smc/application/src/notifications/useToast";
import classNames from "classnames";

type Props = {
    onCreated?: (project: Project) => void,
    onCancel?: () => void;
    onClose?: () => void
};

export function CreateProjectForm({onCreated, onCancel, onClose}: Props) {
    const {showToast} = useToast();
    const form = useForm<ProjectSettings>({
        mode: 'onChange'
    });
    const {handleSubmit, formState: {errors, dirtyFields, isValidating}, trigger} = form;
    const [isIdEditable, setIsIdEditable] = useState(false);
    const [isSubmitted, setIsSubmitted] = useState(false);
    const {suggestId, isLoading: suggestIsLoading} = useSuggestId(form, !isIdEditable);

    const submit = useCallback(async (data: ProjectSettings, event: BaseSyntheticEvent) => {
        event.preventDefault();

        setIsSubmitted(true);

        return ProjectApi.createProject(data)
            .then(onCreated)
            .catch(error => {
                showToast('Error', 'Failed to create project');
                setIsSubmitted(false);
                throw error;
            });
    }, [onCreated]);

    const disabled = !isEmpty(errors) || isEmpty(dirtyFields) || isValidating || suggestIsLoading || isSubmitted;

    useEffect(() => void trigger(), []);

    return (
        <div data-qa-form-project-new>
            <FormHeader onClose={onClose} text={'Create a new project'}/>
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
                <DescriptionField form={form}/>
                <HomeRegionField form={form}/>
                <DefaultEnvField form={form}/>
                <div className={styles.container}>
                    <Button className={classNames(button.primaryButton, button.button)}
                            type="submit"
                            label="create"
                            disabled={disabled}
                            data-qa-btn-create/>
                    <Button className={classNames(button.cancelButton, button.button)}
                            type="button"
                            icon="sm sm-close"
                            label="Cancel"
                            onClick={onCancel}
                            data-qa-btn-cancel/>
                </div>
            </form>
        </div>
    );
}

