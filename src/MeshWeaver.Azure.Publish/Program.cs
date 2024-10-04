using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Azure.Storage.Blobs;
using MeshWeaver.Search;

namespace MeshWeaver.Azure.Publish
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
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

            rootCommand.Handler = CommandHandler.Create<string, string, string, string>((filePath, blobStorageConnectionString, blobContainerName, address) =>
            {
                // Call your custom task logic here
                Console.WriteLine($"Processing files from path: {filePath}");
                Console.WriteLine($"Blob Storage Connection String: {blobStorageConnectionString}");
                Console.WriteLine($"Blob Container Name: {blobContainerName}");
                Console.WriteLine($"Address: {address}");
            });

            await rootCommand.InvokeAsync(args);
        }

        public static bool Execute(string connectionString, string container, string path, string address)
        {
            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                var blobContainerClient = blobServiceClient.GetBlobContainerClient(container);

                foreach (var inputFile in Directory.GetFiles(path))
                {
                    var filePath = Path.GetRelativePath(path, inputFile);
                    var fileContent = File.ReadAllText(filePath);

                    // Parse metadata and HTML
                    var (article, html) = MarkdownIndexer.ParseArticle(filePath, fileContent, address);

                    // Store metadata and HTML in blob storage
                    var htmlBlobClient = blobContainerClient.GetBlobClient(Path.GetFileName(filePath));

                    htmlBlobClient.Upload(new BinaryData(html));

                    // Set metadata on the HTML blob
                    htmlBlobClient.SetMetadata(article.ToMetadata());
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }


    }
}
