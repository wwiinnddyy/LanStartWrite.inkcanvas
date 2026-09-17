# Fluent UI smoke checks

Windows / .NET 10 / the project's pinned Jalium.UI 26.10.9 are required.
Run from the repository root:

```powershell
dotnet build tools/UiSmoke/UiSmoke.csproj -p:ArtifactsPath="$PWD/.arts/smoke"
& ./.arts/smoke/bin/UiSmoke/debug/LanStartWrite.Inkcanvas.UiSmoke.exe
```

The check window is positioned offscreen and closed on completion. Test preferences
live under the test executable's `test-state` directory, not `%LocalAppData%`.
Do not interpret synthetic routed touch tests as physical touchscreen certification.

Checks cover actual theme parsing, brush identity across theme changes, layout-sized
compact navigation, switch geometry, per-contact touch capture/release/cancellation,
secondary-contact rejection, disabled switches, pen-menu event suppression and selection,
flyout placement calculations, and bounded JSON preference persistence.

For visual acceptance, also inspect the toolbar, all four settings pages and pen menu
in Light/Dark at the target machine's DPI. Physically test mouse, touch and pen input,
mixed-DPI monitor transitions, and window layering while drawing. There is no native
WinUI screenshot-diff baseline in this repository yet.

For an interactive toolbar/settings preview with **isolated preferences**, run:

```powershell
& ./.arts/smoke/bin/UiSmoke/debug/LanStartWrite.Inkcanvas.UiSmoke.exe --preview
```

This starts the real application windows with a fresh `preview-state/preferences.json`
next to the test executable. It neither edits the normal profile nor closes an already
running annotation session. Close the preview toolbar to end the preview.

The current suite contains 46 checks, including responsive settings-row reflow without
control replacement, independent navigation selection/focus, native system-color hydration,
initial framework theme selection, single Click/Command gesture dispatch, cancelled gestures, toolbar contact capture and
release, and batched preference-to-ink synchronization.
