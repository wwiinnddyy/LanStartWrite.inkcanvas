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

Checks cover actual theme parsing, brush identity across theme changes, that the
application's own two floating-surface brushes flip on the same instance when the theme
layer says dark, that all six toolbar tools and the pen menu's slider / radio / combo
really resolved to FluentJalium's templates (checked by template part name, because a
missed implicit style falls back silently and stays green), layout-sized compact
navigation, switch geometry, per-contact touch capture/release/cancellation,
secondary-contact rejection, disabled switches, pen-menu event suppression and selection,
the eraser menu (mode radios, radius clamp, clear-all confirmation), flyout placement
calculations, bounded JSON preference persistence, and the annotation overlay's wiring to
the Dusk ink engine (the five pen commands, both erase modes, clear, undo/redo
availability, and the two preferences — pressure and 笔锋 — landing one dispatcher turn
later).

The 笔锋 group is the largest one, and it is deliberately about **wiring, not shaping**:
that a preset choice reaches the live settings, that a manual tweak drops the preset
membership to 自定义, that a custom preset can be saved/loaded/deleted through the
preference file, that the pen menu disables the row for the two uniform-width pen kinds,
that a reload puts custom presets back into the engine's library without duplicating them,
and — the one that matters most — that the generated parameter panel has exactly one
slider per entry in the engine's parameter table. The shaping algorithm itself is Dusk's
business and is covered by the engine's own windowless probes.

Nothing here drives a real stroke: the engine's input path is exercised by Dusk's own
windowless probes, and what is left — 落笔手感、点擦边界、漫游时的 Z 序 — needs a pen on
the target machine. For 笔锋 specifically, what still needs a real hand is whether each
built-in preset *feels* right at the target machine's sampling rate, and whether the
试写区 in the settings page keeps up while a parameter is dragged.

For visual acceptance, also inspect the toolbar, all four settings pages (the 墨迹 page
now carries 18 generated sliders, so check it in Light/Dark at the narrowest supported
window width) and the pen menu in Light/Dark at the target machine's DPI. Physically test
mouse, touch and pen input, mixed-DPI monitor transitions, and window layering while
drawing. There is no native WinUI screenshot-diff baseline in this repository yet.

For an interactive toolbar/settings preview with **isolated preferences**, run:

```powershell
& ./.arts/smoke/bin/UiSmoke/debug/LanStartWrite.Inkcanvas.UiSmoke.exe --preview
```

This starts the real application windows with a fresh `preview-state/preferences.json`
next to the test executable. It neither edits the normal profile nor closes an already
running annotation session. Close the preview toolbar to end the preview.

The suite is 139 checks on the current tree (the count grows with every wiring surface
that gets a guard, and the number printed on a run is only the part that ran before the
first failure), including responsive settings-row reflow without control replacement,
independent navigation selection/focus, native system-color hydration, initial framework
theme selection, single Click/Command gesture dispatch, cancelled gestures, toolbar
contact capture and release, batched preference-to-ink synchronization, the eraser
secondary menu (both erase modes, the radius clamp, and the two-click clear-all), the
笔锋 chain end to end, and the overlay's undo/redo surface.

Two of them used to be timing-sensitive and measured to fail on some machines regardless
of the ink engine (an unmodified `ce8013f` run produced 48 passes with the first one
failing, and a second run died at the fifth). Both are now de-raced, at a price worth
naming:

- the pane reflow check measures width under `ReduceMotion = true`, because
  `PART_PaneRoot` carries a 0.2 s width transition and "it should read 48 next step" is
  a timing question, not a layout one. Whether the two states actually swap is left to
  `IsCompact` and the `PART_Label` collapse check;
- the navigation indicator check asserts that both animation clocks are attached rather
  than reading a position. The FluentJalium animator writes the new destination into the
  *base* value and animates over it, so the old "the base should still read the start"
  premise is gone. Sampling the stretch instead of the position was tried and went red
  once in three runs, so it was removed: **"the indicator resumes from the currently
  displayed geometry" is no longer verified by this suite**, and is not claimed.

`Check` throws, so one failure aborts the rest of the queue — read the pass count, not
just the exit code.

Note on the run command above: `-p:ArtifactsPath` does not actually redirect this
project's output on every SDK, so the executable to run may be the one under
`tools/UiSmoke/bin/<config>/net10.0-windows/`. Running a stale `.arts/smoke` executable
silently exercises an **older revision of the checks** and reports a plausible-looking
pass count — check the timestamp before believing a red or a green.
