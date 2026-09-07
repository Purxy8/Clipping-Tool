# Quiet-desktop capture startup fix — 2026-09-07

## Reproduction

The previous pending recording completed successfully before this separate
startup failure. The affected one-hour Replay session started at 1080p/60,
hit the eight-second output-throughput watchdog, retried at Source, hit the
watchdog again, and then exhausted its capture probes.

The exact production WGC probe on the reference PC passed with motion, but
failed with `runtime probe timed out` under a controlled static primary-display
surface (10.596 seconds). The same 1080p/cursor-on graph on an uncovered,
changing desktop passed in 3.804 seconds. Probe and live video arguments were
identical for 30-second and one-hour retention: retention was not the cause.

## Implementation

Mapped, unrotated displays now prefer a runtime-verified Desktop Duplication
source with `dup_frames=1`. It maintains output when the desktop is unchanged.
WGC and GDI remain capability-checked compatibility paths. Frame-clock behavior
is documented in [FFmpeg's ddagrab reference](https://ffmpeg.org/ffmpeg-filters.html#ddagrab).

DXGI outputs are matched by device name and exact desktop bounds, including
adapter identity. On the reference PC, primary DISPLAY1 was WinForms monitor
index 3 but DXGI adapter 0/output 0. Reusing the WinForms ordinal is incorrect.
The DDA source runs in the output filter graph: a live verbose experiment
confirmed that a lavfi input ignored the supplied hardware filter device and
created its own default device. Audio input numbering accounts for the absence
of an input video stream.

Output-throughput/gap monitoring and bounded recovery remain active. DDA
internally repeats quiet frames, so telemetry does not label its output rate
as distinct desktop updates. GDI promotion accepts verified DDA replacements.
Recorder topology recovery retries an unavailable DDA mapping; persistent
unsupported geometry uses safe Stop/Save rather than launching an unmapped
capture target. No update package or installed user settings were changed.

## Live results

Reference: Windows, RTX 5080, four displays, primary 2560x1440 at 165 Hz.
Desktop audio and microphone were enabled for the capture/service tests.
Release builds completed without warnings/errors and all 105 unit tests passed.

| Case | Result |
| --- | --- |
| DDA static startup, Source/1080p, cursor on, one-hour retention | Both passed in approximately 4 seconds |
| Static Replay, Source | 10 seconds, 600 decoded frames; save 637 ms; no recovery |
| Static Replay, 1080p | 10 seconds, 600 decoded frames; save 608 ms; no recovery |
| Moving Replay, Source | 360 frames, max frame delta 17 ms, identical-frame ratio 2.2%; save 619 ms |
| Moving Replay, 1080p | 360 frames, max frame delta 17 ms, identical-frame ratio 1.9%; save 595 ms |
| Movement-to-idle Replay, 1080p | 600 decoded frames; save 614 ms; no recovery |
| Movement-to-idle Recorder, 1080p | 12.550 seconds, 753 decoded frames; Stop/Save 852 ms; no pending session |
| Movement-to-idle Recorder, Source | 12.617 seconds, 757 decoded frames; Stop/Save 908 ms; no pending session |

The transition harness animates during startup and the first four seconds
after Start completes, then stops drawing for the remainder of a twelve-second
hold. It verifies duration, decoded frame count and production A/V validation.
Reports and generated clips are retained locally under the ignored
`artifacts/startup-diagnosis` directory.

The release containment check terminated only a disposable capture-smoke
parent after six seconds of live DDA/1080p/60 desktop-audio capture. Its verified
direct FFmpeg child exited within 101 ms; four segment files remained unchanged
for two further seconds. The installed ClipForge process was left running.

An initial Recorder test incorrectly equated process creation with the first
captured frame: a 12.600-second file was compared with 13.298 seconds of process
lifetime. The file covered the full 12.014-second post-start hold. The harness
now bounds duration by that observed hold and process lifetime separately,
reports startup overhead, and retains frame-count and A/V checks.

## Limits

These are short real capture tests with one-hour retention configured, not a
completed one-hour or twelve-hour soak. Saturated-GPU gaming, Apex/CS2 exclusive
fullscreen transitions, rotated monitors, live adapter remapping, and other GPU
vendors still need target-machine testing. No claim of eliminating every
possible game-capture stall is made. Existing large-file save complexity is not
changed by this startup fix.

## Re-run

Use the repository SDK/build workflow, then run the capture-smoke project with:

```text
--dda-idle-smoke --resolution 1080p --audio --microphone --artifacts artifacts/startup-diagnosis
--dda-idle-smoke --recorder --motion-first-seconds 4 --resolution source --audio --microphone --artifacts artifacts/startup-diagnosis
```

The diagnostic surface is bounded to twenty seconds and Escape cancels it.
The normal motion matrix can test production selection by omitting
`--force-wgc`; its historical command name is still `--wgc-matrix`.
