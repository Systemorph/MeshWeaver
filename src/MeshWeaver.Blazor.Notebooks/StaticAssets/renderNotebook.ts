import { JupyterLab } from '@jupyterlab/application';
import {
    NotebookPanel,
    NotebookModel,
    Notebook,
    INotebookModel
} from '@jupyterlab/notebook';
import { ServiceManager } from '@jupyterlab/services';
import { LabShell } from '@jupyterlab/application';
import { RenderMimeRegistry } from '@jupyterlab/rendermime';
import { IEditorMimeTypeService } from '@jupyterlab/codeeditor';
import { Context, DocumentRegistry } from '@jupyterlab/docregistry';
import { ILanguageInfoMetadata } from '@jupyterlab/nbformat';
import { IDisposable } from '@lumino/disposable';

class NotebookModelFactory implements DocumentRegistry.IModelFactory<INotebookModel> {
    private _isDisposed = false;

    get isDisposed(): boolean {
        return this._isDisposed;
    }

    dispose(): void {
        this._isDisposed = true;
    }

    createNew(): INotebookModel {
        return new NotebookModel();
    }

    readonly contentType: string = 'notebook';
    readonly fileFormat: string = 'json';
    readonly name: string = 'notebook';

    preferredLanguage(): string {
        return 'python';
    }
}

export class NotebookManager {
    // ... previous code ...

    async createNotebook(): Promise<NotebookPanel> {
        const model = new NotebookModel();
        const content = new Notebook({
            rendermime: this.rendermime,
            contentFactory: Notebook.defaultContentFactory,
            mimeTypeService: this.mimeTypeService
        });

        const factory = new NotebookModelFactory();

        const context = new Context<INotebookModel>({
            manager: this.serviceManager,
            factory: factory,
            path: 'untitled.ipynb'
        });

        const panel = new NotebookPanel({
            content,
            context
        });

        this.shell.add(panel, 'main');
        return panel;
    }
}