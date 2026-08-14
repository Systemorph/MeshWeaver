# MeshWeaver.NuGet.AzureBlob

Azure Blob Storage implementation of the MeshWeaver NuGet package cache. Lets a multi-silo
deployment share one resolved-package cache instead of each pod downloading independently.

## Features

- `BlobNuGetPackageCache` — `INuGetPackageCache` over an Azure Blob container
- `BlobNuGetPackageCacheExtensions` — registration on the host

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
