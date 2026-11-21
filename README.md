# UtilitaryBTSink (.NET 8 / WinUI 3 / WASAPI)

Lightweight, multi-device Bluetooth A2DP Sink audio receiver for Windows 10 (2004+) and Windows 11, engineered for stability:

1. **Multi-device simultaneous audio:** Independent `AudioPlaybackConnection` sessions with automatic multi-stream startup stabilization.
2. **Per-device Auto / Manual connect:** Automatically connects saved paired devices when in range while respecting manual disconnects.
3. **WASAPI Anti-Idle Keep-Alive:** Dedicated MTA COM worker injects sub-audible dither (-96 dBFS) or `AUDCLNT_BUFFERFLAGS_SILENT` during prolonged silence so Windows does not suspend the A2DP stream.
4. **Clean, merged title bar UI:** Lightweight native WinUI 3 interface with collapsible **Settings** and **Logs** panels.
5. **Strict 2-session logging:** Keeps only `current-session.log` and `last-session.log` in `%LocalAppData%\UtilitaryBTSink\logs\`, removing older logs automatically.
6. **System tray & Windows startup:** Native Win32 `Shell_NotifyIconW` tray icon and silent startup (`--minimized`).

## Build Requirements (Only Needed to Compile)
- Windows 10 version 2004 (Build 19041) or Windows 11 (x64)
- Visual Studio 2022 (Community Edition is free) with the **"Windows App SDK"** desktop development workload

## 1-Click Automatic Install (Recommended)
1. Extract the project folder anywhere on your PC.
2. Double-click **`Install.bat`**.
3. The installer automatically compiles **Release | x64 (Self-Contained)** via Visual Studio MSBuild, installs the app to `%LocalAppData%\Programs\UtilitaryBTSink\`, creates **Start Menu** and **Desktop** shortcuts with the official icon, enables **Start minimized with Windows**, and registers an entry in **Windows Settings -> Apps** for clean removal.
4. Once installed, you can delete the extracted folder — **UtilitaryBTSink** runs standalone without Visual Studio.

## Build a Single-File Setup.exe (Optional — Inno Setup 6)
- After running `Install.bat` (or building `Release | x64`), open `Installer\UtilitaryBTSink.iss` in **Inno Setup 6** and click **Build -> Compile** to generate `Installer\Output\UtilitaryBTSink-Setup.exe`.

## Diagnostics & Logs
- Click **Logs** in the top bar to view, copy, or run audio endpoint diagnostics; toggle **View Last Session** to inspect the previous run.
- Logs are stored in `%LocalAppData%\UtilitaryBTSink\logs\` (`current-session.log` and `last-session.log`).
