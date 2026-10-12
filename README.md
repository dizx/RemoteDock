# RemoteDock

A Windows remote desktop connection manager built with C#, .NET 10, and WPF.

## A better fit for older applications

RemoteDock is especially useful for older applications that struggle with Windows
DPI scaling: tiny text, clipped controls, or broken layouts on high-resolution
screens. Use **Scaled Fit** with **Remote DPI / scale** set to **100%** to let the
application render at its normal scale, then enlarge the whole remote desktop
locally to fit your window.

For example, on a 4K panel, **1/2 resolution** requests a 1920 × 1080 remote
desktop and scales it up to fill the panel. This makes older interfaces easier
to read without relying on the application's own high-DPI support.

## Features

- Saved RDP and Hyper-V console connections, search, and independent session tabs.
- Connection groups with inherited settings, mRemoteNG XML / `.rdp` import,
  and alphabetical or last-connected sorting.
- Hyper-V VM discovery and state display, Start/Resume and Save state actions,
  provider-reported progress, and on-demand local administrator elevation.
- Black session-status screens with large green text and stable progress updates.
- **Scaled Fit**: request a fraction of the panel's physical pixel dimensions
  (1/2 by default), then scale locally to fill it. Presets: **1/4, 1/3, 1/2, 2/3,
  3/4, 1/1**. A 3840 × 2160 panel requests 1920 × 1080 at 1/2 or 2560 × 1440 at
  2/3. Resizing the panel updates the remote resolution after a 300 ms pause.
- **Actual Size** with scrolling, or **Match Panel** with dynamic remote resizing.
- **Focus Mode** hides navigation, tabs, and normal toolbars. A slim local strip
  provides session switching and an always-visible way back.
- Full-screen window, clipboard sharing, and optional remote Windows-key routing.
- Window size, position, maximized state, and optional reopening of previous tabs.
- Optional RDP password storage in Windows Credential Manager; passwords are not
  written to connection profiles. Hyper-V guest sign-in stays inside the console.

## Build and run

Requirements: Windows x64, .NET SDK 10.0.401 (or a later 10.0.4xx patch), and the
.NET Framework 4.8 SDK tools available through Visual Studio Installer.
Windows supplies the RDP ActiveX control (`mstscax.dll`).
RemoteDock runs with your normal Windows permissions. Hyper-V management requires
access on the host, such as Hyper-V Administrators membership, rather than mandatory
administrator elevation for the entire app.
When local Hyper-V access is missing, RemoteDock offers to restart as administrator
through Windows UAC and retry the action. Cancelling keeps the current app open.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Generate-RdpInterop.ps1
dotnet build RemoteDock.slnx -c Release
dotnet run --project src/RemoteDock.App -c Release
```

The interop script generates wrappers from your local Windows RDP type library
under `artifacts/interop`. Run it once before building on a new machine.

### Tests

```powershell
dotnet test tests/RemoteDock.Core.Tests -c Release
dotnet run --project tests/RemoteDock.SmokeTests -c Release
```

The smoke check opens local windows, activates RDP controls offline, and exercises
connection failure against a disposable loopback endpoint. It does not contact
a remote host or VM. Run it in an interactive Windows session.
The full suite moves the mouse and opens/closes windows; let it run without
interacting with those windows. Focused checks are also available:

```powershell
dotnet run --project tests/RemoteDock.SmokeTests -c Release -- --session-status-only
dotnet run --project tests/RemoteDock.SmokeTests -c Release -- --hyperv-only
dotnet run --project tests/RemoteDock.SmokeTests -c Release -- --elevation-only
```

These cover progress-screen stability and transport failures, simulated Hyper-V
lifecycle operations, and simulated elevation handoff plus window persistence.

For a real Hyper-V cycle, run this explicitly from an elevated terminal with the
GUID of a local, initially saved VM already in your connection catalog:

```powershell
dotnet run --project tests/RemoteDock.SmokeTests -c Release -- --live-hyperv <VM-GUID> artifacts/diagnostics/live-cycle
```

This resumes the VM, connects using its saved settings, checks native display
stability, and saves it again. It records a timestamped log and a screenshot.
Save-progress checks detect even temporary shrinking of the black status cover
that would expose the underlying RDP surface.
The cycle was verified against a local Generation 1 Windows VM, including the
host's actual saved-state value (`EnabledState = 6`) and provider progress.

## Installer

To build the Windows x64 installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
alongside the build requirements above, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1
```

The script generates the RDP wrappers, publishes a self-contained Release build
with the .NET runtime, and compiles `installer/RemoteDock.iss`. For a custom Inno
Setup location, pass `-IsccPath "C:\path\to\ISCC.exe"`.

Output: `artifacts/installer/RemoteDock-1.0.13-win-x64-Setup.exe`.
Version metadata is defined in `Directory.Build.props`.
The installed application and its shortcuts use `RemoteDock.exe`.

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

RemoteDock remembers its window size, position, and maximized state on exit.
Closing in full screen preserves the window placement from before full screen.

RemoteDock remembers open connection tabs as you work. After a normal exit or an
unexpected shutdown, the next launch asks whether to reopen them. Choose **Yes**
to reconnect using the current saved settings and credentials, or **No** to start
with no sessions. Connections you explicitly close or delete are not reopened.
The recovery list contains only connection IDs in `%LOCALAPPDATA%\RemoteDock\sessions.json`;
passwords are not stored there.

**Remote DPI / scale** in the connection or group editor defaults to **Same as
local PC** (the Windows scaling of the monitor displaying RemoteDock). Select a
percentage from 100% to 500% to request a different text and icon size inside the
remote Windows session. This is independent of the Scaled Fit resolution fraction.
It inherits with group display settings and is preserved when importing an RDP
file's `desktopscalefactor`. Reconnect after changing it; some remote systems
require signing out and back in before Windows applies a different DPI.

Create a connection, choose its display mode, save, and select **Connect**.
Choose the fraction under **Scaled Fit resolution** in the connection editor.
Connection profiles live in `%LOCALAPPDATA%\RemoteDock\connections.json`.
Username/domain can be saved. For RDP, choose whether to remember a password in
Windows Credential Manager or enter it when connecting.

For Hyper-V, specify the **host** address and port **2179**, then click **Load VMs**
and select the virtual machine by its name and state. RemoteDock stores its GUID
internally, so renaming the VM does not break the connection. Loading uses your
Windows identity and requires host WMI access. For manual entry, expand
**Advanced: virtual machine ID**; find the
GUID on the host with:

```powershell
Get-VM | Select-Object Name, Id
```

The account needs VM console access on that host. Basic consoles use the guest's
display resolution; an enhanced session must be supported by the host and guest
to provide desktop-style resolution negotiation. There is no silent fallback
between these modes.

When connecting to an off or saved VM, RemoteDock offers to **Start** or **Resume**
it before opening the console. Closing a Hyper-V tab or exiting RemoteDock asks
whether to **Save state**; choose **No** to disconnect and leave the VM as it is.
Right-click a saved connection or its session tab for **Start/Resume** and
**Save state**, along with an explicit **VM state** line. If local permissions
prevent loading the state, the menu offers **Restart as administrator…**.
These actions use your Windows identity and require Hyper-V
management permissions and WMI/DCOM access to the host. Saving preserves the VM's
memory for the next resume; there is no turn-off or shutdown action.

Connecting, disconnected, and failed sessions show a black screen with large
green status text. Start/resume and save operations show Hyper-V's reported
percentage in that session area; some hosts report 0% until the job completes.
100% is shown only after the target VM state is confirmed.
The bottom status bar stays on one line, with full details in its tooltip.

### Changes in 1.0.13

- Fixed loss of pending RDP tabs when a Hyper-V connection requests elevation
  during previous-session restoration. The restart preserves both open tabs and
  connections still waiting to reopen.
- Cancelling elevation continues restoring the remaining connections. Connections
  explicitly cancelled during restoration stay closed.

### Changes in 1.0.12

- Fixed white flashing during save/resume progress updates. The black status
  cover now keeps its full size while the text changes.
- Saved-VM detection recognizes WMI state `6` as well as legacy `32769`, so
  connecting offers Resume and save completion is recognized correctly.
- Added regression checks for progress-cover stability before and after console
  disconnect, plus an opt-in live Hyper-V resume/connect/save check.

### Hyper-V sign-in

Hyper-V uses the Windows account running RemoteDock to access the host. That
account needs console access, such as Hyper-V Administrators membership. No host
username, domain or password is entered in Hyper-V connection settings.
After adding your account to Hyper-V Administrators, sign out of Windows and
back in so newly launched apps receive the updated group permissions.

After connecting, sign in directly on the VM's Windows login screen. RemoteDock
does not save or type guest credentials. Regular RDP connections retain their
normal credential-saving support.

Use the visible controls to leave Focus Mode/full-screen when the remote control
captures keyboard input. F11 and Ctrl+Shift+F also work when the local WPF UI has focus.
Reconnect applies edited profile settings and asks for credentials again.

## Design and evidence

- [Implementation checklist](docs/implementation.md)
- [REA observations and platform research](docs/research/findings.md)

Mixed-DPI monitor moves, broader Hyper-V host/guest compatibility, and bandwidth
improvements still need endpoint-specific validation. RDP Gateway configuration
is not currently supported.
