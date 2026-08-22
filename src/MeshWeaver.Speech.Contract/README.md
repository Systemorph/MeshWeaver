# MeshWeaver.Speech.Contract

The speech-to-text seam for MeshWeaver: the abstraction a compiled surface depends on so the
implementation can arrive as a module — or not at all.

Kept separate from `MeshWeaver.Speech` on purpose. Every surface that offers voice resolves
`ISpeechTranscriber` **optionally** and degrades when it is absent (the mic hides, the transcribe
endpoint answers 503), so those hosts must be able to name the interface without carrying the
Whisper client. While the two lived in one assembly the portal had to reference the
implementation, which kept it in the app closure: the bits shipped either way and
`Modules:Assemblies` controlled nothing.

## Features

- `ISpeechTranscriber` — transcribe audio; cold `IObservable<SpeechTranscript>`, so the HTTP
  round-trip runs on the bounded I/O pool on subscribe
- `SpeechTranscript` — the recognised text plus the resolved language when the server reports it
- `SpeechTranscriptionOptions` — per-call language, content type and file name; omitted values
  fall back to the implementation's own configuration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
