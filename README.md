# Orbit

**A radial app launcher for Windows.** Press a hotkey and your apps fan out round your cursor as slim glass
bars, rising like a sunrise around a dome that shows the time. Click one to open it.

## Features

| | |
|---|---|
| **Opens at your cursor** | Press **Ctrl+Alt+W** (you can change it) or launch Orbit again. It lives in the tray and comes up in about a tenth of a second. |
| **Pages that turn like a dial** | Eight apps per page. Turn to the next page with the mouse wheel, **Tab** or **Page Down**: the dial rotates and the next page rises from under the horizon. |
| **Warm hover glow** | Bars are clear glass at rest. Hovered, a bar pops out and fills with warm gold-to-rose light with a fine film grain. |
| **Time dome** | The hub shows the time and date. Hover it and it turns into **Close / Esc**. |
| **Folder flyout** | Middle-click File Explorer to browse the folders of your C:\ drive right from the wheel, or middle-click a folder you've added to browse inside it. |
| **Drag to arrange** | Drag a bar to move it. Turn the page while dragging to move an app to another page. |
| **Edit mode** | Drop apps, shortcuts or files from Explorer onto the ring to add them, and remove them with their ✕ badge. |
| **Per-app options** | Right-click a bar: Run as administrator, Open file location, Rename, Always run as administrator, Remove from wheel. |
| **Keyboard** | Arrow keys go round, **Enter** or **1–9** opens, **Tab** / **Shift+Tab** turns, **F2** renames, **Esc** closes. |
| **Stays out of the way** | Clicking outside the wheel closes it, and that click doesn't fall through to the window behind. |
| **Smooth** | Drawn on the GPU and cached, so the animations run at your display's refresh rate. |

## Download

1. Download **`Orbit.exe`** from the [latest release](../../releases/latest). It's a single file; nothing to install.
2. Put it anywhere you like (for example `Documents\Orbit`) and run it.

Needs Windows 10 or 11. Orbit isn't signed, so Windows may show *"Windows protected your PC"* the first time:
click **More info**, then **Run anyway**.

## Use it

1. Run `Orbit.exe`. It goes to the tray.
2. Press **Ctrl+Alt+W**. The first time, the wheel is empty: right-click its centre and choose **Add apps…**
   to pick from your installed apps, or drop shortcuts onto the ring.
3. Right-click the centre for **Edit wheel**, **Start with Windows**, **Open settings file** and **Quit Orbit**.

Command line: `Orbit.exe --background` starts it in the tray without showing the wheel, `--edit` opens the
wheel in edit mode and `--quit` closes a running Orbit.

## Build

Needs only Windows 10 or 11: Orbit is C# and WPF, compiled with the .NET Framework 4.8 compiler that ships with
Windows. No Visual Studio or .NET SDK.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `bin\Orbit.exe` (if Orbit is running, the script asks it to quit first).
`tools\make_icon.py` draws `assets\orbit.ico`; it only runs if the icon is missing and needs Python with Pillow.

## Settings

Everything is saved to `%APPDATA%\Orbit\settings.json`, and Orbit picks up hand edits as soon as you save.

| Key | Default | |
|---|---|---|
| `hotkey` | `Ctrl+Alt+W` | The key that opens the wheel. |
| `layout` | `fan` | `fan` (half wheel) or `circle` (full wheel). |
| `style` | `slim` | `slim` bars or broad `wedge` slices. |
| `outerRadius` | `330` | How far the wheel reaches, in pixels (110–340). |
| `holeRatio` | `0.39` | Size of the centre, as a share of the radius (0.25–0.6). |
| `sliceWidth` | `48` | Width of a bar (28–90). |
| `shortRays` | `0.7` | Length of every other bar, as a share of the long ones (0.4–1). |
| `perFace` | `8` | Apps per page (2–12). |
| `gap`, `roundness` | `6`, `6` | Space between slices and corner rounding. |
| `accent` | `#8AB4FF` | Highlight for drop targets. |
| `apps` | | Your apps: `name`, `path`, optional `args`, `icon` and `admin`. |

The log is `%APPDATA%\Orbit\orbit.log`, and icons are cached in `%LOCALAPPDATA%\Orbit\icons`.

## License

[MIT](LICENSE)
