# mRemoteNG and Windows RDP findings

## Shipped-artifact observations (REA)

Target: `C:\Program Files (x86)\mRemoteNG\mRemoteNG.exe`.
Windows version resource reports `1.76.20.24615` (read separately using PowerShell).

- EXE SHA-256: `6ec4c234894ac6ca598477e45b6eb5c187b1b75e250a6c78954805463bda17b9`.
- REA observed PE/CLI with IL-only and 32-bit-required flags.
- EXE metadata inspection was **partial**: the parser hit its 64 MiB representation
  budget. No claims about EXE methods or reconstructed behavior follow from that scan.
- Evidence: `ev_a3848197bd7cd5816a706a51ea7cb9319a332b36ff12464ed5f03f4e2599c0c6`.
- Bundled `AxInterop.MSTSCLib.dll` metadata was admitted completely. It references
  `System.Windows.Forms` and `Interop.MSTSCLib`, consistent with WinForms ActiveX hosting.
- Wrapper SHA-256: `8647d3a99b3550a16052819511df282733b3f076095ad5aea5a543cd57c2b8bd`.
- Evidence: `ev_6247be177da446b34db18139ccfee0bff12bdaa9f33d8269a3901e2d3b894ae0`.

Full records are retained in `mremoteng-evidence.json`. These are static observations,
not runtime verification. Neither mRemoteNG nor a remote session was executed by REA.

## Source observations

Upstream reviewed at commit `c018afed8dd51f585d534262a46a89332d37ce48`
on `v1.78.2-dev`. This is a different version from the installed executable.

- `RdpProtocol.cs`: hosts an AxMsRdpClient control, sets DesktopWidth/Height,
  and exposes AdvancedSettings2.SmartSizing. Its SmartSize path requests the
  physical screen resolution, so SmartSizing alone is not a lower-resolution policy.
- `RdpProtocol8.cs`: separates control resize from remote-session resize.
- `RdpProtocol7.cs`: Hyper-V configuration uses AuthenticationServiceClass
  `Microsoft Virtual Console Service`, CredSSP, disabled security-layer negotiation,
  and PCB containing the VM GUID; enhanced mode appends `;EnhancedMode=1`.
- `mRemoteNGDocumentation/howtos/vmrdp.rst`: documents host port 2179 and VM ID.

Source base: https://github.com/mRemoteNG/mRemoteNG/tree/c018afed8dd51f585d534262a46a89332d37ce48

## Platform documentation and design conclusions

- [SmartSizing](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings-smartsizing)
  is local fitting and can change while connected.
- [ActiveX hosting in WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/walkthrough-hosting-an-activex-control-in-wpf)
  uses WindowsFormsHost with an AxHost wrapper.
- [Hyper-V enhanced sessions](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enhanced-session-mode)
  use RDP and depend on host/guest support.
- A [WinUI 3 RDP sample](https://github.com/castorix/WinUI3_ActiveX_MSRDP)
  demonstrates an ATL native-window hosting approach; it does not establish
  production readiness for this project's focus/DPI/tab requirements.

Chosen design: WPF around Windows' RDP control, with fresh interop wrappers generated
from the local Windows type library. Display policy belongs in a separate testable
core. Following the user's live-test clarification, Scaled Fit requests a percentage
of the local viewport dimensions and dynamically updates that request after resizing.
The 1080p versus 4K pixel ratio is 1:4; bandwidth improvement remains unmeasured.

## Remaining unknowns

Current Hyper-V endpoint compatibility, basic-versus-enhanced console resolution
behavior, mixed-DPI rendering, keyboard routing, and actual network savings need
runtime checks with designated test hosts.

## Live RDP investigation

The user-designated RDP host accepted Windows authentication. The first build
reported a 1920 x 1080 desktop, but visual inspection showed that it remained at
native size centered within a larger viewport. Native window inspection corroborated
this: the outer RDP control was 3573 x 2010 while its rendered desktop window was
1920 x 1080. This was live UI/Windows Automation evidence, separate from REA's
static artifact evidence above.

The [extended settings documentation](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property)
documents `EnableZoom` (boolean, set before connection) to permit SmartSizing to
upscale beyond native resolution. This is distinct from `ZoomLevel`, which is
mutually exclusive with SmartSizing. RemoteDock now enables `EnableZoom` for
Scaled Fit. The remote desktop is resized proportionally using
`UpdateSessionDisplaySettings`; local fitting continues during the debounce interval.

After the change, native viewport inspection showed a 3448 x 1854 rendered desktop
for a 1724 x 927 remote session. A subsequent window resize changed the reported
remote desktop to 1072 x 592 for a 2148 x 1184 panel, corroborating proportional
remote resizing and local upscaling. The server rounded requested widths slightly.
The final live Focus Mode probe was interrupted by disconnect reason 3 (another
connection to the same remote computer), so that transition is not claimed as
live-verified. These findings are Windows UI Automation observations, not REA records.
