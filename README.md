# PartyTime

**A beat-synced Windows desktop visualizer with click-through overlays, edge glows, and spectrum effects.**

![PartyTime Windows desktop audio visualizer](Assets/GitHub-cover.png)

PartyTime turns music, videos, and other system audio into a light show across your displays. It uses WASAPI loopback to listen to your default Windows playback device. The overlay passes clicks through to your apps, fades during silence, and reconnects when your default playback device changes.

## Quick start

To run from source, install the .NET 10 SDK on Windows.

```powershell
git clone https://github.com/jihadkhawaja/PartyTime.git
cd PartyTime
dotnet run --project PartyTime.csproj
```

1. Play music or a video through your default Windows audio output.
2. Click **Start party** or press **Ctrl + Alt + P**.
3. Try a preset in **Visuals**, or mix effects using the settings tabs. The preview uses sample motion so you can try settings with the overlay off.
4. Press the same shortcut to stop. Closing the controls sends PartyTime to the system tray. To quit, right-click its tray icon and choose **Exit PartyTime**.

## Customize

| Tab | What you can change |
| --- | --- |
| **Visuals** | Apply a visual preset or mix bars and waves, circles, borders, and overlay effects. Set the size and intensity, and choose 20, 30, 45, or 60 FPS or **Match display**. |
| **Images** | Add PNG, JPEG, or BMP images as stickers, image halos, floating particles, or replacements for dots and sparks. Set each image's size, opacity, copies, position, and audio range, and remove a background color with chroma key. |
| **Motion** | Toggle a mouse cursor trail and an independent animated shape layer. Choose trail style, shape, lifetime, and size; customize shape type, motion, fill, count, size, and opacity. |
| **Audio** | Focus on Full Mix, Bass, Vocals, Mids, Treble, or a custom range from 40 to 13,000 Hz. Assign separate frequency ranges to bars, circles, borders, overlay effects, shapes, and cursor pulses. Use **Response** to adjust how strongly the visuals move. |
| **Colors** | Pick Neon, Sunset, Aurora, or two custom colors. **Screen** samples your primary display, and **Mix** blends those colors with your palette. |
| **App** | Turn on **Start with Windows** to launch PartyTime in the system tray at sign-in. This is off by default. |

Each visual layer has its own **None** option. Try **Edge bars**, **Radial wave**, **Double rail**, and **Sparks** together. For a border-only effect, pick a border, set the other three layers to **None**, and turn off the Motion layers. To make the overlay follow voices more closely, choose **Audio → Vocals**, which emphasizes the 300–3,400 Hz range.

In **Visuals → Visual presets**, choose **Neon party**, **Geometry dance**, **Stardust**, **Heartbeat**, **Aurora rain**, **Cosmic spiral**, **Minimal glow**, or **Cursor comet**, then click **Apply preset**. Each preset combines layers, color palettes, and audio assignments. Presets keep your imported images and their customization, audio focus, response, and frame rate. All preset settings can be edited afterward.

In **Motion**, **Show cursor trail** enables a **Glow**, **Ribbon**, or **Shapes** trail behind your mouse. The trail follows the cursor across displays, fades when you stop moving, and works during silence while the party is on. Cursor updates follow each display's refresh rate up to 120 Hz, independently of the music effects' FPS; if refresh detection is unavailable, the cursor targets 60 Hz. Music effects keep their selected frame rate, and only displays touched by the trail receive extra cursor redraws. **Audio → Cursor pulses** chooses which frequencies add pulses. Trail lifetime ranges from 150 to 2,000 ms. Turning the toggle off clears the trail immediately.

**Show animated shapes** adds circles, squares, triangles, diamonds, stars, hexagons, hearts, crosses, pentagons, octagons, or a mix of all ten. Choose **Float**, **Orbit**, **Rain**, **Beat burst**, or **Spiral**, with filled or outlined shapes. Set 4–96 shapes and choose their size and opacity. **Audio → Animated shapes** controls their independent frequency response. Both Motion layers are off by default for existing settings, and both appear in the sample style preview.

In **Audio → Audio range per layer**, choose **Low** (40–250 Hz), **Mid** (250–2,000 Hz), **High** (2,000–13,000 Hz), or **Full mix** for each part. For example, let the circle pulse with bass, the border follow instruments, and sparks react to cymbals. **Follow focus**, the default, uses the frequency focus selected above, including your custom range. Independent ranges keep working even when the global focus excludes those frequencies.

In **Images**, click **Add image…** to select one or more images, then select an image to customize it. **Sticker** places a single image, **Image halo** repeats it around a circle, and **Floating images** spreads copies across each display. **Replace dots / sparks** substitutes your artwork for the particles in **Visuals → Dot halo** and **Sparks**; enable either visual to see the replacement. Each image has its own audio range. You can disable or remove images individually and use images with all four built-in layers set to **None**.

Use **Ctrl+click** to select several images, **Shift+click** for a range, or **Select all** (also **Ctrl+A** while the list is focused). Change a setting to apply just that setting to every selected image. Other settings remain individual. A dash or **Mixed** marks values that differ; mixed sliders show the preview image's value, which you can apply to the selection by clicking the slider. The preview shows the first selected image. Color picking also applies to the selection, and **Remove selected** removes the selected images together. Select one image to rename it.

Images in a shared halo or floating layout alternate in list order, each taking the next available slot. Each image keeps its own copy count. Copies shrink when needed to fit the space between neighbors. Stickers sharing a position are arranged side by side; separately positioned stickers keep their chosen anchors.

To remove an opaque background, enable **Remove a color (chroma key)**, click **Pick from image**, then click the background color in the preview. The picker shows the original image so you can select a color that has already been removed. Adjust **Color tolerance** to include similar shades, or type a hex color directly. The preview's checkerboard shows transparency. Color removal applies to the overlay and is saved per image; disabling the checkbox restores the original image.

PNG transparency and aspect ratio are preserved. Size is a percentage of each display's shorter side, and position is a percentage of its width and height. Add up to 16 images with up to 64 copies each. PartyTime keeps local copies in `%LOCALAPPDATA%\PartyTime\Images`, so moving the original file does not break the overlay. Images must be under 20 MB and no larger than 16,384 pixels on either side; imported copies are scaled to at most 1,024 pixels on the longer side.

The overlay defaults to 30 FPS. **Match display** targets the refresh rate Windows reports for each display and uses 30 FPS when the refresh rate is unavailable. Higher frame rates make motion smoother and use more graphics resources. All frame rates use full-resolution effects.

**Cycle through colors** is on by default. It moves colors through the overlay and shifts them forward on beats. Turning it off keeps each part assigned to the same color, while **Screen** and **Mix** can still pick up changing screen colors.

Visual settings are saved in `%LOCALAPPDATA%\PartyTime\settings.json`. If you move the executable after enabling startup, toggle **Start with Windows** off and back on from the new location.

## Desktop and capture behavior

- PartyTime works over the Windows desktop, including browser fullscreen and borderless fullscreen windows. Exclusive fullscreen apps and the Windows secure desktop can bypass desktop overlays.
- Audio is analyzed in memory. Apps routed to a different playback device are outside the captured mix.
- **Screen** and **Mix** sample colors locally while the overlay is active. They exclude the overlay from sampling to prevent color feedback, so it may be hidden from screenshots and recordings. Use **Palette** mode when you want it visible in captures.

## Project layout

| Folder | Responsibility |
| --- | --- |
| `Views/` | WPF windows and their code-behind |
| `Controls/` | The live style preview control |
| `Audio/` | System audio capture, frequency analysis, and audio data types |
| `Visualization/` | Overlay windows, effect rendering, and screen color sampling |
| `Settings/` | Visualizer preferences and local persistence |
| `Interop/` | Win32 window placement, capture exclusion, the global hotkey, and Windows startup registration |
| `Assets/` | App icons and project artwork |

## Build a Windows executable

In VS Code, press **Ctrl + Shift + B** to run **Publish PartyTime (Windows x64)**. For ARM devices, use **Terminal → Run Build Task → Publish PartyTime (Windows ARM64)**. From PowerShell, run:

```powershell
./scripts/Publish-Windows.ps1 -Architecture x64
./scripts/Publish-Windows.ps1 -Architecture arm64
```

The self-contained executables are written to `artifacts/win-x64/PartyTime.exe` and `artifacts/win-arm64/PartyTime.exe`. Each publish task stops any PartyTime instance running from its own output directory before replacing the executable. Closing the controls only hides PartyTime in the tray; for a manual `dotnet publish`, choose **Exit PartyTime** from the tray first to avoid an access-denied error.

Publishing a GitHub release builds and attaches executables for both architectures.

## Verify changes

Run `dotnet run --project Tests/PartyTime.Tests.csproj` on Windows to check frequency isolation, independent onset detection, settings compatibility, image spacing, bulk image edits, chroma key, the preview eyedropper, shape geometry, cursor fading and monitor coordinates, preset behavior, transparent blending in WPF and Direct2D, and control initialization. Rendered UI checks and a preset gallery are written to `artifacts/feature-checks`.

Run `dotnet run --project Tests/PartyTime.Tests.csproj -c Release -- --benchmark-cursor` to measure cursor rendering time and managed allocations for each trail style. It uses a dense simulated trail on a 1,920 × 1,080 hardware render target; these timings measure rendering work, not end-to-end mouse input latency.

## License

PartyTime by [Jihad Khawaja](https://github.com/jihadkhawaja) is released under the [MIT License](LICENSE).
