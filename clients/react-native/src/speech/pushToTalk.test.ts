import { describe, expect, it, vi } from "vitest";
import { PushToTalkController, type SpeechFlowState, type ThreadSubmitter } from "./pushToTalk";
import type { Recorder } from "./recorder";
import type { AudioInput } from "./transcription";

// The speech → thread-submit flow, driven end-to-end against a fake recorder, a fake transcriber
// (the centralized endpoint), and a fake mesh client — proving: dictation lands in the composer,
// push-to-talk submits to the ACTIVE thread, starts a thread when none, whitespace never submits,
// and every fault surfaces through the visible state machine (never swallowed).

const wav: AudioInput = { uri: "file:///tmp/audio.wav", contentType: "audio/wav", fileName: "audio.wav" };

function fakeRecorder(overrides: Partial<Recorder> = {}): Recorder {
  return {
    start: vi.fn(async () => {}),
    stop: vi.fn(async () => wav),
    cancel: vi.fn(async () => {}),
    ...overrides,
  };
}

const fakeTranscriber = (text: string) => ({ transcribe: vi.fn(async () => ({ text })) });

function fakeSubmitter(): ThreadSubmitter & { submitMessage: ReturnType<typeof vi.fn>; startThread: ReturnType<typeof vi.fn> } {
  return {
    submitMessage: vi.fn(async () => "msg-1234"),
    startThread: vi.fn(async () => ({ path: "rbuergi/_Thread/new-thread-ab12" })),
  };
}

describe("PushToTalkController", () => {
  it("dictation: record → transcribe → text goes to the composer, with visible state transitions", async () => {
    const states: SpeechFlowState[] = [];
    const onTranscript = vi.fn();
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("Grüezi mitenand"),
      onTranscript,
      onStateChange: (s) => states.push(s),
    });

    await ptt.start();
    const result = await ptt.stopInto("composer");

    expect(result).toEqual({ text: "Grüezi mitenand", outcome: "composer" });
    expect(onTranscript).toHaveBeenCalledWith("Grüezi mitenand");
    expect(states).toEqual(["recording", "transcribing", "idle"]); // the UI renders each of these
  });

  it("push-to-talk with an active thread: transcript submits to THAT thread (hub.SubmitMessage twin)", async () => {
    const submitter = fakeSubmitter();
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("and another thing"),
      submitter,
      namespacePath: "rbuergi",
      getActiveThreadPath: () => "rbuergi/_Thread/open-1",
    });

    await ptt.start();
    const result = await ptt.stopInto("submit");

    expect(submitter.submitMessage).toHaveBeenCalledWith("rbuergi/_Thread/open-1", "and another thing");
    expect(submitter.startThread).not.toHaveBeenCalled();
    expect(result.outcome).toBe("submitted");
    expect(result.threadPath).toBe("rbuergi/_Thread/open-1");
  });

  it("push-to-talk with NO active thread: starts one under the namespace and adopts it", async () => {
    const submitter = fakeSubmitter();
    const onThreadStarted = vi.fn();
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("start a new conversation"),
      submitter,
      namespacePath: "rbuergi",
      getActiveThreadPath: () => null,
      onThreadStarted,
    });

    await ptt.start();
    const result = await ptt.stopInto("submit");

    expect(submitter.startThread).toHaveBeenCalledWith("rbuergi", "start a new conversation");
    expect(submitter.submitMessage).not.toHaveBeenCalled();
    expect(onThreadStarted).toHaveBeenCalledWith("rbuergi/_Thread/new-thread-ab12");
    expect(result).toEqual({ text: "start a new conversation", outcome: "started", threadPath: "rbuergi/_Thread/new-thread-ab12" });
  });

  it("whitespace-only transcript submits NOTHING (never enqueue an empty round)", async () => {
    const submitter = fakeSubmitter();
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("   "),
      submitter,
      namespacePath: "rbuergi",
    });

    await ptt.start();
    const result = await ptt.stopInto("submit");

    expect(result.outcome).toBe("empty");
    expect(submitter.submitMessage).not.toHaveBeenCalled();
    expect(submitter.startThread).not.toHaveBeenCalled();
    expect(ptt.state).toBe("idle");
  });

  it("a transcription fault surfaces on the state machine and never reaches the mesh", async () => {
    const submitter = fakeSubmitter();
    const errors: (string | undefined)[] = [];
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: { transcribe: vi.fn(async () => Promise.reject(new Error("HTTP 503 model not loaded"))) },
      submitter,
      namespacePath: "rbuergi",
      onStateChange: (s, e) => s === "error" && errors.push(e),
    });

    await ptt.start();
    await expect(ptt.stopInto("submit")).rejects.toThrow("HTTP 503");
    expect(ptt.state).toBe("error");
    expect(errors).toEqual(["HTTP 503 model not loaded"]);
    expect(submitter.submitMessage).not.toHaveBeenCalled();
    expect(submitter.startThread).not.toHaveBeenCalled();
  });

  it("a submit fault also surfaces (the transcript is not silently lost — the error carries on)", async () => {
    const submitter = fakeSubmitter();
    submitter.submitMessage.mockRejectedValueOnce(new Error("Access denied"));
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("hello"),
      submitter,
      namespacePath: "rbuergi",
      getActiveThreadPath: () => "rbuergi/_Thread/open-1",
    });

    await ptt.start();
    await expect(ptt.stopInto("submit")).rejects.toThrow("Access denied");
    expect(ptt.state).toBe("error");
  });

  it("a recorder start fault surfaces and releases the mic", async () => {
    const recorder = fakeRecorder({ start: vi.fn(async () => Promise.reject(new Error("Microphone permission denied."))) });
    const ptt = new PushToTalkController({ recorder, transcriber: fakeTranscriber("x") });
    await expect(ptt.start()).rejects.toThrow("permission denied");
    expect(ptt.state).toBe("error");
    expect(recorder.cancel).toHaveBeenCalled();
  });

  it("cancel abandons the recording without transcribing or submitting", async () => {
    const recorder = fakeRecorder();
    const transcriber = fakeTranscriber("should never be used");
    const submitter = fakeSubmitter();
    const ptt = new PushToTalkController({ recorder, transcriber, submitter, namespacePath: "rbuergi" });

    await ptt.start();
    await ptt.cancel();

    expect(recorder.cancel).toHaveBeenCalled();
    expect(transcriber.transcribe).not.toHaveBeenCalled();
    expect(submitter.submitMessage).not.toHaveBeenCalled();
    expect(ptt.state).toBe("idle");
  });

  it("stopInto without a recording in flight throws (guards double-release)", async () => {
    const ptt = new PushToTalkController({ recorder: fakeRecorder(), transcriber: fakeTranscriber("x") });
    await expect(ptt.stopInto("composer")).rejects.toThrow("Not recording.");
  });

  it("direct submit without a mesh connection fails loud, not silent", async () => {
    const ptt = new PushToTalkController({
      recorder: fakeRecorder(),
      transcriber: fakeTranscriber("hello"),
      // no submitter — the offline/dictation-only configuration
    });
    await ptt.start();
    await expect(ptt.stopInto("submit")).rejects.toThrow(/requires a mesh connection/);
  });
});
