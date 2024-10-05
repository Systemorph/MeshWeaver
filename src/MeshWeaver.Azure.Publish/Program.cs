using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text.Json;
using Azure.Storage.Blobs;
using MeshWeaver.Search;
using Microsoft.Extensions.Logging;

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
                    "--address",
                    "The address to use")
            };

            rootCommand.Description = "MeshWeaver Build Tasks";

            rootCommand.Handler = CommandHandler.Create<string, string, string, string>(async (path, connectionString, container, address) =>
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(container) || string.IsNullOrEmpty(address))
                {
                    logger.LogError("Error: All options (--path, --connection-string, --container, --address) must be provided and cannot be null or empty.");
                    return;
                }

                // Call your custom task logic here
                logger.LogInformation($"Processing files from path: {path}");
                logger.LogInformation($"Blob Storage Connection String: {connectionString}");
                logger.LogInformation($"Blob Container Name: {container}");
                logger.LogInformation($"Address: {address}");

                await ExecuteAsync(connectionString, container, path, address, logger);
            });

            await rootCommand.InvokeAsync(args);
        }

        public static async Task<bool> ExecuteAsync(string connectionString, string container, string path, string address, ILogger logger)
        {
            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(container);

                // Create the container if it doesn't exist
                await blobContainerClient.CreateIfNotExistsAsync();

                var localFiles = Directory.GetFiles(path).Select(f => Path.Combine(address, Path.GetRelativePath(path, f))).ToHashSet();
                var blobs = blobContainerClient.GetBlobs().ToDictionary(b => b.Name);

                // Delete blobs that no longer exist in the local directory
                foreach (var blob in blobs)
                {
                    if (!localFiles.Contains(blob.Key))
                    {
                        logger.LogInformation($"Deleting blob {blob.Key} as it no longer exists in the local directory.");
                        await blobContainerClient.DeleteBlobAsync(blob.Key);
                    }
                }

                foreach (var inputFile in Directory.GetFiles(path))
                {
                    var filePath = Path.Combine(address, Path.GetRelativePath(path, inputFile));
                    var fileContent = await File.ReadAllTextAsync(inputFile);
                    var fileLastModified = File.GetLastWriteTimeUtc(inputFile);

                    // Check if the blob exists and if the local file is newer
                    if (blobs.TryGetValue(filePath, out var blobItem) && blobItem.Properties.LastModified >= fileLastModified)
                    {
                        logger.LogInformation($"Skipping upload for {filePath} as the blob is up-to-date.");
                        continue;
                    }

                    logger.LogInformation($"Uploading file {inputFile} to {filePath}");

                    if (Path.GetExtension(filePath) == "md")
                        await ParseAndUploadHtml(address, logger, filePath, fileContent, blobContainerClient);

                    // upload the file
                    var markdownBlobClient = blobContainerClient.GetBlobClient(filePath);
                    await markdownBlobClient.UploadAsync(new BinaryData(fileContent), overwrite: true);

                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during execution.");
                return false;
            }
        }

        private static async Task ParseAndUploadHtml(string address, ILogger logger, string filePath, string fileContent,
            BlobContainerClient blobContainerClient)
        {
            // Parse metadata and HTML
            var (article, html) = MarkdownIndexer.ParseArticle(filePath, fileContent, address);

            // Store metadata and HTML in blob storage
            var htmlFilePath = Path.ChangeExtension(filePath, ".html");
            var htmlBlobClient = blobContainerClient.GetBlobClient(htmlFilePath);
            await htmlBlobClient.UploadAsync(new BinaryData(html), overwrite: true);

            // Set metadata on the HTML blob
            var metadata = article.ToMetadata();
            logger.LogInformation($"{JsonSerializer.Serialize(metadata)}");
            await htmlBlobClient.SetMetadataAsync(metadata);
        }
    }
}
