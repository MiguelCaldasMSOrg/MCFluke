# MCFluke.Display

A display-only .NET 10 Windows Forms application for the Fluke 187. This is
an additional project in `MCFluke.sln`; existing projects are unchanged.

## Run

Open the solution in Visual Studio 2026, build **Debug | x64** or
**Release | x64**, set **MCFluke.Display** as the startup project, and press
**F5**. Select **Fluke 187 - COM3** for the real meter, or explicitly select
**Simulator** for `sim://187`. Stop any other process using the serial port.
The existing managed/native project references build and deploy `mcfluke.dll`.
No extra NuGet dependencies or console window are required.

The window opens centered on the display containing the mouse pointer. Its
initial outer width and height are each one third of that display's dimensions.
It has a black background, white reading, no caption, buttons, menus, or other
controls, and only the standard resize border. Drag the border to resize.

The value uses the meter's displayed text, preserving signs, trailing zeros,
prefixes, and out-of-range/text states without numeric reformatting or SI
conversion. Units appear alongside it at exactly half the value font size,
aligned to the same baseline. Both are centered and fitted to the available
space whenever the reading or window dimensions change.

Press any mouse button or touch the display area once to exit. Border drags
remain available for resizing. The window hides immediately; shutdown releases
the meter after any already-running native operation finishes, bounded by the
existing two-second operation timeout.

Only the connection's read-only identity verification and consecutive `QM`
display queries are sent. There is no polling timer or added delay: the next
query follows the preceding reply and its brief UI handoff. A background worker
keeps serial I/O off the UI thread. Changed readings use awaited `InvokeAsync`
dispatch, so updates and their errors are observed without accumulating a UI
backlog; unchanged readings do not dispatch redundant UI work.
No meter buttons or settings are changed.
The actual rate depends on the meter and optical/serial connection.

Connection, protocol, and native-library errors replace the reading with a
visible diagnostic and stop polling. There is no simulated or stale-reading
fallback. Click or tap to dismiss the error; the process returns exit code 1
(2 for invalid arguments).

Other ports can be selected in the project's Debug settings or on the command
line:

```powershell
.\csharp\MCFluke.Display\bin\Debug\net10.0-windows\MCFluke.Display.exe --port COM4
```

## Offline checks

The **Offline self-tests** launch profile opens only `sim://187`. It checks
display preservation, monitor centering, fitting at different aspect ratios,
font ratio and baseline alignment, resize-only chrome, first-button/touch
dismissal, continuous simulator polling, and cancellation.

To see the check output and exit status after building the solution:

```powershell
dotnet .\csharp\MCFluke.Display\bin\Debug\net10.0-windows\MCFluke.Display.dll --self-test
```

The form also has a parameterless, connection-free Designer construction path.
Only the runtime startup supplies a port and starts polling.

These are this project's own checks; existing projects and their check runners
are not modified. Simulator checks do not establish a physical meter's maximum
update rate or optical-link reliability.
