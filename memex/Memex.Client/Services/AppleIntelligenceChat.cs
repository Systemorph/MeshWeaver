#if IOS || MACCATALYST
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using MeshWeaver.Mesh.Threading;

namespace Memex.Client.Services;

/// <summary>
/// On-device text generation via <b>Apple Intelligence</b> — the OS <c>FoundationModels</c> framework
/// (iOS 26 / macOS 26 on Apple-Intelligence-capable hardware). The app ships <b>no</b> model; the OS
/// provides it (lean). The async FoundationModels call is bridged through a tiny native Swift shim that
/// exposes a C ABI (<c>Platforms/{iOS,MacCatalyst}/AppleIntelligence.swift</c>) and is run on the
/// <see cref="IIoPool"/> so the UI never blocks.
/// <para>Degrades safely: if the shim isn't linked in (or the OS/hardware lacks Apple Intelligence) the
/// P/Invoke throws and <see cref="Availability"/> reports <c>Unavailable</c>, so the app falls back to the
/// connected mesh — nothing crashes.</para>
/// </summary>
internal sealed class AppleIntelligenceChat : IOnDeviceChat
{
    // The Swift shim is compiled INTO the app binary, so its C-ABI exports resolve via the main image.
    private const string Lib = "__Internal";

    [DllImport(Lib, EntryPoint = "memex_ai_available")]
    private static extern int NativeAvailable();

    [DllImport(Lib, EntryPoint = "memex_ai_respond")]
    private static extern IntPtr NativeRespond([MarshalAs(UnmanagedType.LPUTF8Str)] string prompt);

    [DllImport(Lib, EntryPoint = "memex_ai_free")]
    private static extern void NativeFree(IntPtr ptr);

    private readonly IIoPool _pool;

    public AppleIntelligenceChat(IIoPool pool) => _pool = pool;

    public OnDeviceChatAvailability Availability =>
        SafeAvailable() ? OnDeviceChatAvailability.Available : OnDeviceChatAvailability.Unavailable;

    private static bool SafeAvailable()
    {
        try { return NativeAvailable() != 0; }
        catch { return false; }   // shim not linked / OS too old / hardware unsupported → fall back to mesh
    }

    public IObservable<string> Respond(string prompt) =>
        Availability != OnDeviceChatAvailability.Available
            ? Observable.Empty<string>()
            : _pool.InvokeBlocking(_ => RespondBlocking(prompt));

    private static string RespondBlocking(string prompt)
    {
        var ptr = NativeRespond(prompt);
        if (ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUTF8(ptr) ?? string.Empty; }
        finally { NativeFree(ptr); }
    }
}
#endif
