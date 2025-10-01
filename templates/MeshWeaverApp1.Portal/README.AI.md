# AI Configuration

This application supports multiple AI providers for the chat functionality.

## GitHub Models (Recommended for Development)

GitHub Models provides free access to various AI models for development and experimentation.

### Setup

1. Get a GitHub token with appropriate permissions:
   - Go to [GitHub Settings > Developer settings > Personal access tokens](https://github.com/settings/tokens)
   - Create a new token with `model.request` scope
   - Or use GitHub CLI: `gh auth token`

2. Set the environment variable:
   ```bash
   # Windows (Command Prompt)
   set GITHUB_TOKEN=your_github_token_here

   # Windows (PowerShell)
   $env:GITHUB_TOKEN="your_github_token_here"

   # macOS/Linux
   export GITHUB_TOKEN=your_github_token_here
   ```

3. Run the application - it will automatically use the GitHub token if available.

### Available Models

GitHub Models supports various models including:
- `gpt-4o-mini` (recommended for development)
- `gpt-4o`
- `gpt-3.5-turbo`

Update the `Models` array in `appsettings.Development.json` to use your preferred model.

## Azure OpenAI

For production deployments, you can use Azure OpenAI:

1. Update `appsettings.json` with your Azure OpenAI configuration:
   ```json
   {
     "AzureInference": {
       "Url": "https://your-resource.openai.azure.com",
       "ApiKey": "your-api-key",
       "Models": ["your-deployment-name"]
     }
   }
   ```

## Environment Variables

The application checks for the following environment variables:

- `GITHUB_TOKEN`: GitHub personal access token for GitHub Models
- Configuration values can also be overridden via environment variables using the format:
  - `AzureInference__Url`
  - `AzureInference__ApiKey`
  - `AzureInference__Models__0`

## Priority Order

1. Environment variables (highest priority)
2. `appsettings.{Environment}.json`
3. `appsettings.json` (lowest priority)

The application will automatically use GitHub Models if a `GITHUB_TOKEN` environment variable is set, making it easy to switch between development and production configurations.