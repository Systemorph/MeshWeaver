// iOS copy of the Apple Intelligence (FoundationModels) C-ABI bridge — see
// Platforms/MacCatalyst/AppleIntelligence.swift for the build-integration notes. Identical source.

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
