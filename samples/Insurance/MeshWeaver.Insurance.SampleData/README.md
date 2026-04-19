# MeshWeaver.Insurance.SampleData

## Overview
MeshWeaver.Insurance.SampleData is a console application that generates and updates sample Excel files used by the Insurance domain. It populates property risk data (65+ global office locations) into structured `.xlsx` worksheets while preserving header rows, totals, and freeze panes.

## Features
- Generates realistic property risk location data for sample insurance submissions
- Supports inspecting existing Excel files to review their structure
- Creates timestamped backups before modifying files
- Uses ClosedXML for Excel manipulation

## Usage
```bash
# Update the sample Microsoft.xlsx with generated data
dotnet run --project samples/Insurance/MeshWeaver.Insurance.SampleData

# Inspect an existing Excel file
dotnet run --project samples/Insurance/MeshWeaver.Insurance.SampleData -- inspect
```

## Related Projects
- [MeshWeaver.Insurance.Domain](../MeshWeaver.Insurance.Domain/) -- Insurance domain model and services
