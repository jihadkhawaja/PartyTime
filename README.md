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
3. Change the effect in **Visuals**, **Audio**, or **Colors**. The preview uses sample motion so you can try settings with the overlay off.
4. Press the same shortcut to stop. Closing the controls sends PartyTime to the system tray. To quit, right-click its tray icon and choose **Exit PartyTime**.

## Customize

| Tab | What you can change |
| --- | --- |
| **Visuals** | Mix bars and waves, circles, borders, and overlay effects. Set the size and intensity, and choose 20, 30, 45, or 60 FPS or **Match display**. |
| **Audio** | Focus on Full Mix, Bass, Vocals, Mids, Treble, or a custom range from 40 to 13,000 Hz. Use **Response** to adjust how strongly the visuals move. |
| **Colors** | Pick Neon, Sunset, Aurora, or two custom colors. **Screen** samples your primary display, and **Mix** blends those colors with your palette. |
| **App** | Turn on **Start with Windows** to launch PartyTime in the system tray at sign-in. This is off by default. |

Each visual layer has its own **None** option. Try **Edge bars**, **Radial wave**, **Double rail**, and **Sparks** together. For a border-only effect, pick a border and set the other three layers to **None**. To make the overlay follow voices more closely, choose **Audio → Vocals**, which emphasizes the 300–3,400 Hz range.

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

In VS Code, press **Ctrl + Shift + B** to run **Publish PartyTime (Windows x64)**. From PowerShell, run the equivalent command:

```powershell
dotnet publish PartyTime.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

The self-contained executable is written to `artifacts/win-x64/PartyTime.exe`.

## License

PartyTime by [Jihad Khawaja](https://github.com/jihadkhawaja) is released under the [MIT License](LICENSE).
