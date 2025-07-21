# Hidden Collections Configuration Example

You can now hide collections from specific contexts using the `HiddenFrom` property in your configuration.

## Configuration Examples

### appsettings.json
```json
{
  "ContentSourceConfigs": [
    {
      "Name": "Documentation",
      "DisplayName": "Documentation",
      "BasePath": "../../modules/Documentation/Markdown",
      "HiddenFrom": []
    },
    {
      "Name": "InternalDocs", 
      "DisplayName": "Internal Documentation",
      "BasePath": "../../internal/docs",
      "HiddenFrom": ["Articles"]
    },
    {
      "Name": "AdminCollection",
      "DisplayName": "Admin Collection", 
      "BasePath": "../../admin/content",
      "HiddenFrom": ["Articles", "Collections"]
    }
  ]
}
```

### Context Usage

- **"Articles"** context: Used in `ArticlesCatalogPage` - collections with `"Articles"` in `HiddenFrom` won't appear in the articles catalog picker
- **"Collections"** context: Used in `CollectionsPage` via `FileBrowser` - collections with `"Collections"` in `HiddenFrom` won't appear in the file browser picker

### Available Contexts

- `"Articles"` - Article catalog pages
- `"Collections"` - Collection management pages
- Custom contexts can be added as needed

### Behavior

- Empty `HiddenFrom` array `[]` - Collection visible in all contexts
- `["Articles"]` - Hidden only from article catalog pages
- `["Collections"]` - Hidden only from collection management pages  
- `["Articles", "Collections"]` - Hidden from both contexts
