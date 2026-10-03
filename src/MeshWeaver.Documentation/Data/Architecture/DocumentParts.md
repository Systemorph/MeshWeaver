---
Name: Document Parts
Category: Architecture
Description: How a log, transcript or file becomes one logical Document node plus many indexed DocumentPart nodes in their own partition table — written and embedded chunk by chunk while the text is still arriving — and how further node types (annotations) attach to a part.
Icon: DocumentText
---

# Document Parts

Every job, agent round and CI run produces text: progress logs, transcripts, token streams, downloaded
CI logs. This page is the platform's one shape for keeping that text as **durable mesh nodes**,
**searchable while it is still arriving**, without flooding `mesh_nodes` and without touching the
original.

## The shape

```
{collection}/_Documents/{slug}                               Document            (mesh_nodes)
{collection}/_Documents/{slug}/_DocumentPart/000000          DocumentPart        (document_parts)
{collection}/_Documents/{slug}/_DocumentPart/000001          DocumentPart        (document_parts)
{collection}/_Documents/{slug}/_DocumentPart/000001/_PartAnnotation/{id}
                                                             DocumentPartAnnotation (document_part_annotations)
```

- **One document = one logical node** at `DocumentPaths.For(collectionPath, filePath)` — the same path
  the content indexer has always given an indexed file, so `search_chunks` hits link to it unchanged.
- **Many parts**: the text is cut into the content index's windows — **1000 characters, 150 overlap**
  (`DocumentPartPaths.DefaultChunkSize` / `DefaultChunkOverlap`) — and part *N* is exactly
  `chunkIndex` *N* of the content chunk index. A part carries its text, its offset `Start` in the
  original, the SHA-256 of its text, the `(collectionPath, filePath)` key, and whether it reached the
  vector index (`Indexed`, `IndexError`).
- **Its own table.** Parts and annotations are satellites mapped in `SatelliteTableMapping.Defaults`
  (`_DocumentPart` → `document_parts`, `_PartAnnotation` → `document_part_annotations`), so every
  partition schema gets both tables from `public.ensure_partition_schema` and the high-volume rows never
  land in `mesh_nodes`. **The tables belong to the partition that owns the document**: a Space's
  transcripts live in that Space's schema; instance-wide logs (control's jobs) live under `Admin`, in
  Admin's schema.
- **Access** follows the owning partition: a part's `MainNode` is the owner of its first satellite
  segment (`SatelliteTableMapping.OwnerOfSatellitePath`), and the satellite access rule delegates to it.
- **The original stays in storage.** The document references it by `(collectionPath, filePath)`, every
  part by offset and hash. A streamed log's original is assembled once, on completion, and written into
  its content collection; a file that was uploaded or downloaded is never rewritten.

## Written as the text arrives

The document's own hub is the **one writer** of the document. A producer — any hub — appends:

```csharp
// open once (idempotent), then append as text arrives, then seal
hub.OpenDocumentLog(new DocumentLogTarget("Admin/Jobs/build-42/logs", "run.log"))
   .SelectMany(path => hub.AppendToDocument(path, chunkOfText, offset))   // offset = producer's char offset
   ...
hub.CompleteDocument(path);                                               // writes the trailing part
```

As soon as a window is **complete** (the document holds `Start + 1000` characters) the hub writes that
part node, embeds it and upserts it into the vector index — right then, not at the end. The unsealed
remainder (`Tail`, under one window) is held on the document node itself, so nothing lives only in
memory. Completion writes the trailing partial part, seals the document, records the original's hash
and stores the original.

**Idempotent by `(document, index)`.** A part's path and text are a pure function of the original, so
a retried or concurrent flush rewrites the same node with the same content, and the trim that follows a
part write is conditional on the document not having advanced past it. An append that carries the
producer's `Offset` skips whatever the document already holds, so redelivery never duplicates text; an
offset past the end is a gap and is refused.

**Equivalence.** However the text is split into appends, the parts are exactly
`TextChunker.Chunk(fullText, 1000, 150)` — the same windows an upload of the finished file produces.

## Attaching further node types to a part

A node about ONE chunk — a person's note, an agent's label (`root-cause`, `flake`, `noise`), a finding
the bug-fix learning loop consumes — is a `DocumentPartAnnotation` at
`{partPath}/_PartAnnotation/{id}`. A further attached type needs exactly one more
`SatelliteTableMapping` entry: a segment **longer than `_DocumentPart`** (placement picks the longest
mapped segment in the path) and the table it should live in. Existing tables are untouched; the
provisioning proc adds the new table to every partition.

## Placement rule, and the guard

A storage adapter places a satellite by the **longest mapped segment anywhere in its path**.
`_DocumentPart` outranks `_Activity`, `_Thread` and `_Comment`, so a document filed under a job
activity or a thread keeps its parts in `document_parts`. It does not outrank `_ThreadMessage`, and it
ties `_Notification` / `_UserActivity`; `DocumentPartPaths.PartPlacementProblem` refuses a document
filed there, because its parts would be written into another table and never be found again.

## Search

The vector index is the existing content chunk index (`content_chunks`, the store behind
`search_chunks` / `get_chunk`). A part is upserted under its `(collectionPath, filePath, index)`, so
`search_chunks` finds a running job's log while it is still being written and `get_chunk` steps through
it like any indexed file.
