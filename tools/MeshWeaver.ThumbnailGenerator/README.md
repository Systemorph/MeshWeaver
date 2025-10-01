# MeshWeaver Thumbnail Generator

A .NET tool for generating thumbnails of MeshWeaver Layout Areas using Playwright.

## Installation

```bash
dotnet tool install -g MeshWeaver.ThumbnailGenerator
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
pwsh bin/Debug/net9.0/playwright.ps1 install
```