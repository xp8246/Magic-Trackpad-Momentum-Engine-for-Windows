# Magic Trackpad Momentum Engine

Bring macOS-like kinetic scrolling and momentum to Windows!

A lightweight, high-performance physics engine built in pure C# (Win32 API) that perfectly replicates the "glide and sticky stop" momentum scrolling experience of macOS on Windows. Designed specifically for the Magic Trackpad, it features a modern bilingual GUI (English/中文), advanced physics tuning, multi-monitor smart crossing, and app exclusions.

## Features

- **True macOS Feel**: Exponential decay engine with asymmetric friction (independent X/Y axis tuning).
- **Edge Physics (4 Modes)**: Hard Stop, Slide Edge, Dynamic Bounce, or Fixed Rebound.
- **Smart Palm Rejection**: Velocity thresholds and distance filters to ignore typing jitter.
- **Multi-Monitor Crossing**: Adjustable kinetic penalty when sliding across monitor boundaries.
- **App Blacklist**: Automatically disable momentum in specific apps (e.g., Photoshop, Excel) for precise pixel work.
- **Visual Trajectory Preview**: A built-in "Ghost Cursor" debug overlay (Yellow = Real, Cyan = Virtual Inertia) to visualize physics in real-time.
- **KISS Philosophy**: Single C# file, zero external dependencies, no sketchy drivers.

## Installation

No installation required! Simply download the compiled executable from the Releases page.

1. Download `MagicTrackpadEngine.exe` from the Releases tab.
2. Run it! It will silently sit in your system tray.
3. Right-click the tray icon to open Settings or temporarily disable the engine.

## Compilation (For Developers)

Don't want to download a compiled `.exe`? Compile it yourself using the built-in Windows C# compiler in 3 seconds.

Open Command Prompt (cmd) and run:
```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:MagicTrackpadEngine.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll Inertia.cs

Physics Tuning Guide
The engine is highly customizable to match your exact finger feel. Hover over any setting in the app to see a detailed tooltip.

High Speed Float: Determines how far the cursor glides when you flick it fast. Closer to 1.0 = less air resistance.

Low Speed Brake: Controls the "sticky" finish. Lower values mean the cursor stops abruptly at the end, mimicking Mac precision.

Activation Speed: How fast you must flick to trigger the engine.

Tick Rate: Default 10ms (100Hz) perfectly matches the Bluetooth polling limit of the Magic Trackpad.

License
This project is licensed under the MIT License.
