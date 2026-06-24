// Tiny C-ABI bridge to Apple Intelligence (the FoundationModels framework) for AppleIntelligenceChat.cs.
//
// 🛠️  BUILD INTEGRATION (Mac + Xcode, not done by the plain `dotnet build`): this Swift file must be
//     compiled and linked INTO the app binary so its @_cdecl exports resolve via "__Internal". The usual
//     route in a .NET MAUI iOS/MacCatalyst app is a small native binding/static-lib built from this source
//     and referenced via <NativeReference> (Kind=Static). Requires the **iOS 26 / macOS 26 SDK**. Until
//     that's wired, AppleIntelligenceChat reports Unavailable and the app uses the connected mesh — no crash.
//
//     This file is intentionally identical to Platforms/iOS/AppleIntelligence.swift.

import Foundation
#if canImport(FoundationModels)
import FoundationModels
#endif

/// 1 when on-device Apple Intelligence is ready to answer, else 0.
@_cdecl("memex_ai_available")
public func memex_ai_available() -> Int32 {
#if canImport(FoundationModels)
    if #available(iOS 26.0, macCatalyst 26.0, *) {
        if case .available = SystemLanguageModel.default.availability { return 1 }
    }
#endif
    return 0
}

/// Generates a reply to `prompt` and returns it as a malloc'd UTF-8 C string (free with memex_ai_free).
/// Returns nil when unavailable. Synchronous on purpose — the caller invokes it on the IoPool, off the UI.
@_cdecl("memex_ai_respond")
public func memex_ai_respond(_ prompt: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>? {
#if canImport(FoundationModels)
    if #available(iOS 26.0, macCatalyst 26.0, *) {
        let text = String(cString: prompt)
        let semaphore = DispatchSemaphore(value: 0)
        var reply = ""
        Task {
            defer { semaphore.signal() }
            do {
                let session = LanguageModelSession()
                reply = try await session.respond(to: text).content
            } catch {
                reply = ""
            }
        }
        semaphore.wait()
        return strdup(reply)
    }
#endif
    return nil
}

@_cdecl("memex_ai_free")
public func memex_ai_free(_ ptr: UnsafeMutablePointer<CChar>?) {
    if let ptr = ptr { free(ptr) }
}
