# RemoteDock

A Windows remote desktop connection manager built with C#, .NET 10, and WPF.

## First working slice

- Saved RDP and Hyper-V console connections, search, and independent session tabs.
- **Scaled Fit**: request a fraction of the panel's physical pixel dimensions
  (1/2 by default), then scale locally to fill it. Presets: **1/4, 1/3, 1/2, 2/3,
  3/4, 1/1**. A 3840 × 2160 panel requests 1920 × 1080 at 1/2 or 2560 × 1440 at
  2/3. Resizing the panel updates the remote resolution after a 300 ms pause.
- **Actual Size** with scrolling, or **Match Panel** with dynamic remote resizing.
- **Focus Mode** hides navigation, tabs, and normal toolbars. A slim local strip
  provides session switching and an always-visible way back.
- Full-screen window, clipboard sharing, and optional remote Windows-key routing.
- Passwords are supplied per connection and are not saved in connection profiles.

## Build and run

Requirements: Windows x64, .NET SDK 10.0.401 (or a later 10.0.4xx patch), and the
.NET Framework 4.8 SDK tools available through Visual Studio Installer.
Windows supplies the RDP ActiveX control (`mstscax.dll`).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Generate-RdpInterop.ps1
dotnet build RemoteDock.slnx -c Release
dotnet run --project src/RemoteDock.App -c Release
```

The interop script generates wrappers from your local Windows RDP type library
under `artifacts/interop`. Run it once before building on a new machine.

```powershell
dotnet test tests/RemoteDock.Core.Tests -c Release
dotnet run --project tests/RemoteDock.SmokeTests -c Release
```

The smoke check opens local windows and activates the RDP controls offline. It
does not initiate a remote connection. Run it in an interactive Windows session.

## Installer

To build the Windows x64 installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
alongside the build requirements above, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1
```

The script generates the RDP wrappers, publishes a self-contained Release build
with the .NET runtime, and compiles `installer/RemoteDock.iss`. For a custom Inno
Setup location, pass `-IsccPath "C:\path\to\ISCC.exe"`.

Output: `artifacts/installer/RemoteDock-1.0.1-win-x64-Setup.exe`.
Version metadata is defined in `Directory.Build.props`.

Setup offers two installation modes:

- **All users** (default): installs to `C:\Program Files\RemoteDock` and requires
  administrator approval.
- **Current user**: installs to `%LOCALAPPDATA%\Programs\RemoteDock` without
  administrator privileges.

Start menu and optional desktop shortcuts follow the selected installation mode.
For unattended installs, use `/ALLUSERS` or `/CURRENTUSER` with `/VERYSILENT`.
Windows supplies the RDP ActiveX control. Connection profiles remain per-user;
uninstalling preserves them in `%LOCALAPPDATA%\RemoteDock`.

Copyright (c) 2026 Morten Aasheim.

## Connecting

Create a connection, choose its display mode, save, and select **Connect**.
Choose the fraction under **Scaled Fit resolution** in the connection editor.
Connection profiles live in `%LOCALAPPDATA%\RemoteDock\connections.json`.
Username/domain can be saved; passwords are entered at connection time.

For Hyper-V, specify the **host** address, port **2179**, and VM GUID:

```powershell
Get-VM | Select-Object Name, Id
```

The account needs VM console access on that host. Basic consoles use the guest's
display resolution; an enhanced session must be supported by the host and guest
to provide desktop-style resolution negotiation. There is no silent fallback
between these modes. Live Hyper-V interoperability still needs endpoint testing.

Use the visible controls to leave Focus Mode/full-screen when the remote control
captures keyboard input. F11 and Ctrl+Shift+F also work when the local WPF UI has focus.
Reconnect applies edited profile settings and asks for credentials again.

## Design and evidence

- [Implementation checklist](docs/implementation.md)
- [REA observations and platform research](docs/research/findings.md)

This is an initial implementation. Mixed-DPI monitor moves, Hyper-V compatibility,
and bandwidth improvements still require runtime validation with designated test
endpoints. RDP Gateway configuration and connection import
are not part of this first slice.
