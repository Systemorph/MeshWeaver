using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using MeshWeaver.Markdown;

namespace MeshWeaver.Azure.Publish
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger(typeof(Program));

            var rootCommand = new RootCommand
            {
                new Option<string>(
                    "--path",
                    "The input files to process"),
                new Option<string>(
                    "--connection-string",
                    "The connection string for the blob storage"),
                new Option<string>(
                    "--container",
                    "The name of the blob container"),
                new Option<string>(
                    "--html-container",
                    "The name of the blob container containing pre-rendered html"),
                new Option<string>(
                    "--address",
                    "The address to use")
            };

            rootCommand.Description = "MeshWeaver Build Tasks";

            rootCommand.Handler = CommandHandler.Create<string, string, string, string, string>(async (path, connectionString, container, htmlContainer, address) =>
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(container) || string.IsNullOrEmpty(address))
                {
                    logger.LogError("Error: All options (--path, --connection-string, --container, --address) must be provided and cannot be null or empty.");
                    return;
                }

                // Call your custom task logic here
                logger.LogInformation("Processing files from\npath: {path}\nstorage: {connectionString}\ncontainer: {container}\nhtmlContainer: {htmlContainer}\naddress: {address}", path, connectionString, container, htmlContainer, address);
                await ExecuteAsync(connectionString, container, htmlContainer, path, address, logger);
            });

            await rootCommand.InvokeAsync(args);
        }

        public static async Task<bool> ExecuteAsync(string connectionString, string container, string htmlContainer, string path, string address, ILogger logger)
        {
            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                var blobContainer = blobServiceClient.GetBlobContainerClient(container);
                var htmlBlobContainer = htmlContainer == null ? null : blobServiceClient.GetBlobContainerClient(container);

                // Create the container if it doesn't exist
                await blobContainer.CreateIfNotExistsAsync();
                if(htmlContainer != null)
                    await htmlBlobContainer.CreateIfNotExistsAsync();


                var localFiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.Combine(address, Path.GetRelativePath(path, f))).ToHashSet();
                var blobs = blobContainer.GetBlobs(prefix:address).ToDictionary(b => b.Name);

                // Delete blobs that no longer exist in the local directory
                foreach (var blob in blobs)
                {
                    if (!localFiles.Contains(blob.Key))
                    {
                        logger.LogInformation($"Deleting blob {blob.Key} as it no longer exists in the local directory.");
                        await blobContainer.DeleteBlobAsync(blob.Key);
                    }
                }

                foreach (var inputFile in Directory.GetFiles(path))
                {
                    try
                    {
                        var filePath = Path.Combine(address, Path.GetRelativePath(path, inputFile)).Replace('\\', '/');
                        var fileContent = await File.ReadAllTextAsync(inputFile);
                        var fileLastModified = File.GetLastWriteTimeUtc(inputFile);

                        // Check if the blob exists and if the local file is newer
                        if (blobs.TryGetValue(filePath, out var blobItem) && blobItem.Properties.LastModified >= fileLastModified)
                        {
                            logger.LogInformation($"Skipping upload for {filePath} as the blob is up-to-date.");
                            continue;
                        }

                        var extension = Path.GetExtension(filePath);
                        logger.LogInformation($"Uploading file {inputFile} to {filePath}. Parsing as {extension}");

                        var metadata = extension switch
                        {
                            ".md" => await ParseMarkdown(address, logger, filePath, fileContent, htmlBlobContainer),
                            _ => DefaultMetadata(filePath, extension.Trim('.'))
                        };

                        logger.LogInformation("Uploading {filePath} with {metadata}", filePath, JsonSerializer.Serialize(metadata));
                        var blob = blobContainer.GetBlobClient(filePath);
                        await blob.UploadAsync(new BinaryData(fileContent), overwrite: true);
                        await blob.SetMetadataAsync(metadata);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "An error occurred during file upload.");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during execution.");
                return false;
            }
        }

        private static Dictionary<string, string> DefaultMetadata(string filePath, string type)
        {
            return new Dictionary<string, string>
            {
                { "type", type },
                { "path", filePath }
            };
}

        private static async Task<IDictionary<string, string>> ParseMarkdown(string address, ILogger logger, string filePath, string fileContent, BlobContainerClient htmlContainer)
        {
            // Parse metadata and HTML
            var (article, html) = MarkdownIndexer.ParseArticle(filePath, fileContent, address);

            // Set metadata on the HTML blob
            var metadata = article?.ToMetadata(filePath, "md") ?? DefaultMetadata(filePath, "md");

            if (htmlContainer != null)
            {
                // Store metadata and HTML in blob storage
                var htmlFilePath = Path.ChangeExtension(filePath, ".html");
                logger.LogInformation("Uploading pre-rendered html {path}", htmlFilePath);
                var htmlBlobClient = htmlContainer.GetBlobClient(htmlFilePath);
                await htmlBlobClient.UploadAsync(new BinaryData(html), overwrite: true);
                await htmlBlobClient.SetMetadataAsync(metadata);

            }

            return metadata;
        }
    }
}
