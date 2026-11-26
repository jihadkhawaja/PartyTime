# Performance work — 2026-11-17

PartyTime prefers GPU rendering on every desktop: ANGLE/Direct3D or OpenGL on Windows, Metal or OpenGL on macOS, and GLX/EGL on Linux. Software rendering remains a compatibility fallback. Windows screen capture prefers DXGI desktop duplication with GPU downscaling, falling back to GDI for unsupported/rotated displays and capture failures.

The native Windows smoke test observed both a Skia GPU context and the DXGI GPU capture backend. Linux and macOS runtime performance has not been measured on this Windows development machine.

These measurements describe the November 17 optimization. To match the original WPF appearance, the current screen effects crop and blend at capture resolution off the UI thread, then use the renderer to scale and fade the finished image.

## Changes

- Scene geometry is recorded into immutable Skia pictures on the UI thread and replayed by the compositor. The render thread never reads mutable scene state. Imported images share raster/texture identities across displays and the preview.
- Screenshot blending, zoom and fading use renderer operations. The hot overlay path no longer runs a CPU pixel-blending loop. The Screen preview uses the same rendering path without offscreen preview bitmap generation/readback.
- Normal and Negative retain one screenshot per capture generation. Negative conversion happens once per held image. Live blends capture at up to 60 Hz on the hardware path, reuse unchanged frames, and retain only the latest frame instead of accumulating a queue.
- Primary-display color sampling shares the screenshot capture when both features are enabled. Capture and render ownership use explicit leases, so slow/retained draws cannot read overwritten or freed pixel storage.
- Animation follows compositor callbacks. Music and cursor deadlines remain independent, missed deadlines are skipped, and a watchdog provides a fallback when remote compositors stop callbacks. Previews stop when controls are hidden.
- Settings sliders retain capture devices unless a capture feature is actually enabled/disabled. Image layouts and cursor bounds are cached. CPU fallback/reference blend kernels specialize by blend and use portable SIMD when no zoom is needed.
- macOS reuses its video transfer buffer, supports up to 60 Hz for samples, and uses a 4 Hz capture configuration for dedicated palette sampling.

## Measured scene recording

These are local component microbenchmarks at 1920×1080 with a deterministic audio/motion fixture. Baseline records Avalonia drawing objects; the optimized implementation records native Skia pictures. This measures command recording and managed allocations, not GPU execution, present latency, or end-to-end FPS. Each case uses 15 warmups and 100 measured iterations. Short timing samples and other desktop activity affect results.

| Preset | Before mean (ms) | After mean (ms) | Before bytes/frame | After bytes/frame |
| --- | ---: | ---: | ---: | ---: |
| Neon party | 1.265 | 0.167 | 429,616 | 16,720 |
| Geometry dance | 1.641 | 0.069 | 679,096 | 1,968 |
| Stardust | 3.272 | 0.161 | 1,554,344 | 1,072 |
| Heartbeat | 0.179 | 0.021 | 154,600 | 1,072 |
| Aurora rain | 0.322 | 0.047 | 274,536 | 5,240 |
| Cosmic spiral | 1.015 | 0.099 | 797,832 | 2,992 |
| Minimal glow | 0.047 | 0.017 | 49,520 | 3,856 |
| Cursor comet | 1.324 | 0.210 | 1,224,328 | 21,736 |

The benchmark includes a small amount of timing-array bookkeeping in bytes/frame. The largest measured allocation reduction was approximately 99.9%.

## Measured native capture

The live Windows capture benchmark requests 60 frames at scheduled 30 Hz per backend, after warmup. Desktop content is uncontrolled. The capture work is measured at 960×402 for the available display. GDI overran its requested cadence; DXGI was able to service it. These are capture/readback costs, not overall frame rate. GPU downscaling uses high-quality cubic interpolation.

| Backend | Mean capture work (ms) | p95 work (ms) |
| --- | ---: | ---: |
| GDI fallback | 54.486 | 61.507 |
| DXGI GPU downscale | 1.164 | 2.529 |

## Validation and reproduction

- 264 portable checks, including pixel comparisons of all preset pictures against the Avalonia reference renderer; all six screenshot blends and opacity; randomized SIMD/scalar equivalence; retained-frame lifetimes; unchanged-frame reuse; and idle preview shutdown.
- 8 native Windows checks, including GPU context observation, capture exclusion, cursor coordinates, compositor callbacks, capture/readback and WASAPI connection.
- Headless pixel checks use real Skia software rendering to validate output; the native smoke test exercises the compositor separately.

```sh
dotnet build PartyTime.csproj -c Release -warnaserror
dotnet run --project Tests/PartyTime.Tests.csproj -c Release
dotnet run --project Tests/PartyTime.Tests.csproj -c Release -- --benchmark-performance --label latest
# On Windows with an interactive desktop:
dotnet run --project Tests/PartyTime.Tests.csproj -c Release -- --native-smoke
dotnet run --project Tests/PartyTime.Tests.csproj -c Release -- --benchmark-capture
```

The portable benchmark writes `artifacts/benchmarks/performance-<label>.json`. Capture comparison writes `artifacts/benchmarks/capture-performance.json`. Reference measurements from this change are preserved in [before.json](benchmarks/before.json), [after.json](benchmarks/after.json), and [capture.json](benchmarks/capture.json). No screenshots or audio are stored by these benchmarks.

## Immersion optimizations

Zoomed screenshot blends keep the original crop and pixel math. AVX2 gathers source pixels directly into vector lanes. Other SIMD-capable systems reuse a resampled row, and a scalar path covers systems without SIMD. Capture workers reuse pixels and palettes when the capture backend reports the same frame.

Audio analysis keeps the 2,048-sample stereo FFT and 512-sample hop. It computes the same onset state at every hop and only allocates an output frame for the final hop in each packet. The FFT uses double-precision SIMD when available, skips transforms for entirely silent windows, and shares complete frequency-band peaks across ranges. Scene recording reuses gradient buffers and path geometry. Scene smoothing calculates its band coefficients once per update.

These are local Windows x64 component timings. Each result is the median of three alternating baseline and optimized run means. Blends use a 960×540 BGRA frame at 1.04× zoom. Scene recording uses 1920×1080 and a full waterfall history. The runs use the same timing and visual settings and measure CPU work for each listed operation.

| Operation | Before (ms) | After (ms) | Managed bytes before → after |
| --- | ---: | ---: | ---: |
| Zoomed Additive blend | 7.167 | 0.242 | 8 → 8 |
| Zoomed Difference blend | 6.999 | 0.219 | 8 → 8 |
| Zoomed Screen blend | 2.047 | 0.447 | 8 → 8 |
| Zoomed Multiply blend | 2.020 | 0.349 | 8 → 8 |
| Analyze 25 ms of stereo audio | 0.156 | 0.098 | 2,595 → 1,112 |
| Analyze 25 ms of silence | 0.171 | 0.027 | 2,595 → 1,112 |
| Record Spectrum Waterfall | 0.074 | 0.052 | 14,616 → 5,160 |
| Record Warp Tunnel | 0.029 | 0.012 | 4,784 → 1,584 |

Managed allocation figures include 8 bytes per iteration for timing storage. Validation compared all twelve preset renders byte for byte and compared band levels, reaction values, and onsets against the previous analyzer across 3,312 frames. The blend checks compare every channel against the scalar crop and formula, including nonaligned widths and pooled scratch buffers.
