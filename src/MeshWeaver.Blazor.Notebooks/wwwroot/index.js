import { N as NotebookModel, a as Notebook, C as Context, b as NotebookPanel } from "./vendor-r-u5kpiv.js";
class NotebookModelFactory {
  constructor() {
    this._isDisposed = false;
    this.contentType = "notebook";
    this.fileFormat = "json";
    this.name = "notebook";
  }
  get isDisposed() {
    return this._isDisposed;
  }
  dispose() {
    this._isDisposed = true;
  }
  createNew() {
    return new NotebookModel();
  }
  preferredLanguage() {
    return "python";
  }
}
class NotebookManager {
  // ... previous code ...
  async createNotebook() {
    new NotebookModel();
    const content = new Notebook({
      rendermime: this.rendermime,
      contentFactory: Notebook.defaultContentFactory,
      mimeTypeService: this.mimeTypeService
    });
    const factory = new NotebookModelFactory();
    const context = new Context({
      manager: this.serviceManager,
      factory,
      path: "untitled.ipynb"
    });
    const panel = new NotebookPanel({
      content,
      context
    });
    this.shell.add(panel, "main");
    return panel;
  }
}
export {
  NotebookManager
};
