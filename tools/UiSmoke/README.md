# Fluent UI smoke checks

Windows / .NET 10 / the project's pinned Jalium.UI 26.10.9 are required.
Run from the repository root:

```powershell
dotnet build tools/UiSmoke/UiSmoke.csproj -c Debug -p:OutputPath=bin/Verify/
& ./tools/UiSmoke/bin/Verify/LanStartWrite.Inkcanvas.UiSmoke.exe
```

`-p:OutputPath=bin/Verify/` is not decoration: the application's own `.exe` locks
`bin/Debug` while it is running, and a build that lands on a locked output fails or is
silently served from stale bits. The `-p:ArtifactsPath` form used previously does **not**
redirect this project's output on this SDK — an earlier revision of this file told you to
run `./.arts/smoke/bin/UiSmoke/debug/...`, which on this machine stayed a months-old
executable. Running a stale exe silently exercises an **older revision of the checks** and
reports a plausible-looking pass count, so check the timestamp before believing either a
red or a green.

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

The 窗口层级 group (`CheckWindowLayers`) is the one that asks the **operating system**
rather than asking ourselves. It opens four real windows — the canvas shrunk to 320×240
and parked offscreen, so the acceptance run does not actually cover the desktop — then:

- asserts each window's **rank in the desktop Z-order** satisfies
  设置 < 笔菜单 < 批注栏 < 画布 (smaller rank = nearer the front);
- **deliberately breaks it** (`NativeWindowZOrder.PlaceAfter(canvasHandle, HWND_TOP)`, the
  exact shape of the "canvas covered the toolbar" incident), then asserts `Verify()`
  reports it and `Reconcile()` repairs it;
- hides the canvas while the dialog is up and asserts neither the toolbar nor the dialog
  carries `WS_EX_TOPMOST` (the "a dialog pulls the app out of the topmost band" rule);
- shows the canvas again and asserts **both** the canvas and the dialog are topmost —
  the direct consequence of upward pinning inheritance, and its acceptance.

The `Verify()` half only covers *intra-app order* and *band membership*. "No other
application can cover the canvas" is guaranteed by `WS_EX_TOPMOST` itself (topmost is
maintained by the shell; a normal window can never rise above a topmost one), so that
claim is asserted where it actually lives: on the style bit. Whether the periodic
self-check is live is asserted separately near the end of the queue — a timer that was
written but never wired up has no symptom of its own.

The 工具栏 group (`CheckToolbarTools` + the rewritten `CheckToolbarTouch`) covers the data-driven
toolbar. The point of that group is one sentence from the user — "放两个笔按钮，他俩的数据还要独立"
— so the assertions are about *independence*, not about pixels:

- the default list is seven items in a known order, and the six buttons still render at 40×40 with
  the library's focus visual and the accent/on-accent ink of the checked state;
- adding a pen **copies the current one** (it never hands you a blank second pen) and the new button
  really appears on the bar;
- two pens keep separate colour, thickness **and tip shape** — including the hard case:
  hand-tune one, switch away, switch back, and the shape is still there (the check that a
  preset-id-only model cannot pass);
- **editing a tool reaches the engine immediately**: the pen menu's thickness / colour and the eraser
  menu's mode / radius are asserted against the *engine's own* attributes right after the change —
  no tool switch in between. This is a regression guard for a shipped defect where the data model was
  updated but nothing wrote the new value into the engine, so the change only appeared after switching
  tools and back;
- same for two erasers (mode + radius);
- the fixed items (mouse / undo / redo / settings) cannot be deleted, and deleting the *selected*
  item leaves the selection on a usable tool rather than dangling;
- the settings list has exactly one row per item, and its "add a pen" button really grows both the
  data and the list.

The 画布 group (`CheckCanvasModes`) covers the two per-scene canvas modes. Both are "you only see it the
next time you enter the canvas" switches, so every assertion hangs off the enter/leave path rather than
off a toggle's `IsChecked` — a switch that lights up while the behaviour does not change is exactly the
failure this kind of feature ships with:

- 穿透模式: with it on, mouse mode **keeps the canvas on screen** and the canvas gets the full
  click-through recipe — `WS_EX_LAYERED` + `SetLayeredWindowAttributes(255)` + `WS_EX_TRANSPARENT`,
  plus a `WM_NCHITTEST` hook answering `HTTRANSPARENT`. The acceptance is not "we set the bits": the
  check asks the OS (`WindowFromPoint`) which window would receive a click at the canvas's centre and
  asserts it is **not the canvas**. `WS_EX_TRANSPARENT` alone does not route clicks to *other
  processes'* windows, which is why a shipped build passed every style check while still drawing under
  the user's cursor. (What the suite *cannot* judge is whether the layered path preserves the canvas's
  per-pixel alpha — that needs one human look.);
  switching back to a drawing tool clears it — otherwise the pen silently stops responding;
- 冻结模式: entering the canvas captures the screen once and the frozen image's natural size equals the
  canvas size (that is the "lands 1:1, not offset or scaled" check); switching tools inside the same
  visit does **not** re-capture (the screen already has ink on it by then);
- turning either switch off puts the behaviour back, and pass-through also drops the frozen image
  (otherwise you would be looking at a stale frozen screen while clicking the real windows behind it);
- **pass-through counts as "left the canvas"**: the canvas never left the screen while pass-through
  was on, so judging by "is the canvas up" alone would make "went to mouse mode, operated the
  computer, came back" and "switched eraser to pen" look identical — and only the first one should
  re-freeze. That distinction has its own assertion;
- the settings page's two switches really reach the runtime state and the "current behaviour" sentence
  in that page follows them.

Note that this group **really does capture the full screen and show it on the canvas for a moment**
(the canvas is maximised and resizing a maximised window does not take effect). That is the feature
behaving, not a test artifact — but it does mean the acceptance run flashes the screen briefly.

### Whiteboard (second canvas)

`CheckWhiteboardCanvas`, `CheckWhiteboardUndoLands`, `CheckWhiteboardSelect` and
`CheckWhiteboardTransforms` cover the second canvas. What they insist on, in the same
"ask the observable thing" spirit:

- clicking 白板 swaps **which canvas is live** and hides the other one, while both keep their own
  document and undo ledger — the assertion that carries the weight is *"pressing undo takes the
  stroke off the board you are looking at, and leaves the other one's ledger untouched"*;
- tool data is one shared set, read off the live surface **in the same beat** (measured: R=209, 7 px);
  switching scenes refreshes visuals without rebuilding the controls (`ReferenceEquals` on the button,
  because rebuilding would drop keyboard focus);
- the whiteboard ground really is an opaque brush equal to the persisted choice (`FFFFFFFF` →
  `FFDDEEE1` on a swatch change, no re-entry needed);
- selection is driven through **synthetic routed pointer events** raised at the window, and asserted
  as coordinates: a drag inside the frame moves every stroke by exactly the finger delta
  (Δ=(90,45) for a (90,45) drag), **one** undo restores both strokes, a corner drag leaves the
  opposite corner at 0.00 DIP of drift, and a rotate handle moves a corner 0.5 rad around a centre
  that does not move (radius 760 → 760);
- pinch: two contacts 200 px apart pulled to 400 px give `Scale = 2` while the world point under the
  midpoint does not move (600,400 → 600,400 to 1e-6), sliding both fingers 50 px translates the
  content by exactly 50 px without touching the scale, and after 2 → 1 the surviving finger does
  nothing until it lifts. `Document.Count` and the selection are unchanged across the whole gesture;
- eraser radius is converted to world units and **re-applied when the viewport moves**
  (20 px → 10 world at 2×, back to 20 when pinched back to 1:1);
- the annotation canvas stays pinned to 1:1: the wheel and the middle button are taken at the window
  level and the viewport never moves, while a left button press is deliberately **not** handled
  (mouse writing flows through that promotion).

Two assertions in the rotate group were green for the wrong reason before being rewritten —
"the centre does not move" and "the radius is unchanged" also hold when nothing happened at all —
so a rotate check must measure *how far it turned*. The same lesson killed a false report upstream:
"a stroke vanishes after pinching" looked like an engine history-replay defect, and only a clean-board
minimal repro (3 → 3) showed it was this code calling `Undo()` after a zero-change batch.

Nothing here drives a real stroke: the engine's input path is exercised by Dusk's own
windowless probes, and what is left — 落笔手感、点擦边界、漫游时的 Z 序 — needs a pen on
the target machine. For 笔锋 specifically, what still needs a real hand is whether each
built-in preset *feels* right at the target machine's sampling rate, and whether the
试写区 in the settings page keeps up while a parameter is dragged. For 窗口层级, what still
needs a real hand is the interaction the suite cannot reach: whether the canvas actually
stays above a *real* always-on-top application on the target machine, and whether the
self-heal pass is ever visibly noticeable when it fires. For 白板, what still needs a real hand is two-finger recognition timing and feel at the target device's sampling rate, whether a large selection follows the finger without dropped frames, pen feel on a solid ground, and whether the whiteboard really stays above a real always-on-top application. For 工具栏, it is whether two pens
of similar colour are told apart at a glance on the real bar (the colour chip is only
22×3 px) and whether four buttons per row still fit at the narrowest supported window width.

For visual acceptance, also inspect the toolbar, all four settings pages (the 墨迹 page
now carries 18 generated sliders, so check it in Light/Dark at the narrowest supported
window width) and the pen menu in Light/Dark at the target machine's DPI. Physically test
mouse, touch and pen input, mixed-DPI monitor transitions, and window layering while
drawing. There is no native WinUI screenshot-diff baseline in this repository yet.

For an interactive toolbar/settings preview with **isolated preferences**, run:

```powershell
& ./tools/UiSmoke/bin/Verify/LanStartWrite.Inkcanvas.UiSmoke.exe --preview
```

This starts the real application windows with a fresh `preview-state/preferences.json`
next to the test executable. It neither edits the normal profile nor closes an already
running annotation session. Close the preview toolbar to end the preview.

The suite is 215 checks on the current tree (the count grows with every wiring surface
that gets a guard, and the number printed on a run is only the part that ran before the
first failure), including responsive settings-row reflow without control replacement,
independent navigation selection/focus, native system-color hydration, initial framework
theme selection, single Click/Command gesture dispatch, cancelled gestures, toolbar
contact capture and release, batched preference-to-ink synchronization, the eraser
secondary menu (both erase modes, the radius clamp, and the two-click clear-all), the
笔锋 chain end to end, the 窗口层级 chain against the real desktop Z-order, the 画布
per-scene modes, and the overlay's undo/redo surface.

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
