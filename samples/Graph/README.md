# MeshWeaver.Samples.Graph

## Overview
MeshWeaver.Samples.Graph provides sample graph data and static content used by the Memex Portal during development. This project does not produce compiled output -- it exists solely for browsing and organizing sample data files in Visual Studio.

## Contents
- **Data/** -- Sample MeshNode definitions organized by namespace (ACME, Northwind, Cornerstone, etc.), including node types, users, agents, access policies, and activity logs
- **content/** -- Static content files such as icons, images, and attachments served by the portal
- **attachments/** -- File attachments referenced by graph nodes

## Usage
The Memex Portal loads graph nodes from `Data/` via `AddGraph()` at startup. Content files are served as static assets under their respective namespace paths.

## Related Projects
- [MeshWeaver.Graph](../../src/MeshWeaver.Graph/README.md) -- Graph node framework
- [Memex.Portal.Monolith](../../memex/Memex.Portal.Monolith/) -- Development portal that consumes this data
