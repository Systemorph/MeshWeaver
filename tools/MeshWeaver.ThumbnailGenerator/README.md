# MeshWeaver Thumbnail Generator

A .NET tool for generating thumbnails of MeshWeaver Layout Areas using Playwright.

## Running it

Not published as a dotnet tool ([NuGet Package Retirement](../../src/MeshWeaver.Documentation/Data/Architecture/NuGetPackageRetirement.md)).
Run it from a checkout:

```bash
dotnet run --project tools/MeshWeaver.ThumbnailGenerator -- <args>
```

## Usage

### Generate thumbnails from a catalog page:
```bash
meshweaver-thumbnails --catalogUrl https://localhost:65260/@app/Documentation/Catalog --output ./thumbnails
```

### Generate thumbnail for a single area:
```bash
meshweaver-thumbnails --area https://localhost:65260/@app/Documentation/Overview --output ./thumbnails
```

### Options:
- `--catalogUrl`: Full URL to the LayoutArea catalog page
- `--area`: URL of a single area to screenshot (alternative to --catalogUrl)
- `--output`: Output directory for thumbnails (default: ./thumbnails)
- `--dark-mode`: Generate dark mode thumbnails in addition to light mode (default: true)
- `--width`: Thumbnail width in pixels (default: 400)
- `--height`: Thumbnail height in pixels (default: 300)

## Prerequisites

This tool requires Playwright browser binaries to be installed. After installing the tool, run:

```bash
pwsh bin/Debug/net10.0/playwright.ps1 install
```

## How a thumbnail is made

Playwright renders the area at twice the thumbnail size and screenshots it; `ThumbnailResizer`
then fits the content into the `--width` x `--height` box (aspect ratio preserved) and scales the
capture down to that size with a bicubic filter, writing a PNG. The scaling uses SkiaSharp (MIT),
the same stack the portal ships for share cards and favicons. It replaced SixLabors.ImageSharp:
every ImageSharp version up to 4.1.1 carries high-severity advisories, and the patched releases
need a paid licence. `test/MeshWeaver.ThumbnailGenerator.Test` pins the output size and format.