# Fluent UI implementation and verification

## Reference baseline

- Framework: **Jalium.UI 26.10.9**, `.jalxaml` plus code-behind; neither WPF nor WinUI runtime controls.
- WinUI reference: the workspace's `microsoft-ui-xaml` Git object database, commit
  **19e3bdc3c**. Its working tree was not restored or reset.
- Principal source files: `controls/dev/CommonStyles/Common_themeresources_any.xaml`,
  `AppBarButton_themeresources.xaml`, `AppBarToggleButton_themeresources.xaml`,
  `Slider_themeresources.xaml`, `RadioButton_themeresources.xaml`,
  `ToggleSwitch_themeresources.xaml`, and `controls/dev/NavigationView/NavigationView_themeresources.xaml`.
- ModernWpf public implementation and parity methodology were reviewed at
  `https://github.com/Kinnara/ModernWpf/blob/master/docs/winui3-source-parity.md`
  and `ModernWpf.Controls/ToggleSwitch/ToggleSwitch.xaml`. This informs resource/state
  separation and documentation of framework-specific substitutions, not a WPF migration.
- The explicitly requested `C:\git\Jalium` reference directories were located but are
  outside the connected workspace's approved read roots. This review does **not** establish
  equivalence with that inaccessible local ModernWpf checkout or its purported 1.0 revision.

## Covered application controls

The current application surfaces are covered: toolbar radio-tool buttons and settings
button, drag grip, settings navigation, normal/subtle buttons, toggle switches,
sliders, radio buttons, ComboBox/ComboBoxItem, color swatches, text hierarchy, cards,
tooltips and the floating pen menu. This is an application theme, not a port of the
entire WinUI/Jalium control catalog.

Resources live in `src/LanStartWrite.Inkcanvas/Themes/Fluent`. `FluentTheme.Initialize`
loads exact embedded resource names, in dependency order, and fails with the resource
name if a dictionary is missing or malformed. There is no silent partial-theme fallback.

Light and Dark brush values come from the source's corresponding dictionaries.
Application-specific accents are `#0078D4` / `#60CDFF`; hover/pressed accent brushes
retain the same hue at 0.9/0.8 opacity. Theme changes mutate shared brush objects so
existing StaticResource consumers update without recreating controls or their state.

## Deliberate Jalium adaptations

| Component | Implementation decision |
| --- | --- |
| Settings layout | Inner Grid margins supply card spacing; do not rely on this version's Grid.Padding. Only the selected page is attached to the visual tree. |
| Navigation | 220 DIP application pane / 48 DIP compact pane; 36 DIP rows, 40 DIP icon slot, one shared 3 x 16 DIP selection indicator with travelling/stretch animation. Content-root size, not the Window's declared Width, drives the 800 DIP breakpoint. |
| Switch | `FluentToggleSwitch : ToggleButton` supplies native toggle/keyboard automation semantics and separate mouse/touch capture. Template track is 40 x 20; knob is 12, hover 14, pressed 17 x 14; hit area is 48 x 32. Jalium's stock private spring implementation has different fixed geometry. |
| Slider | Retains Jalium's 16 DIP PART_Thumb anchor required by its value-to-position code; draws a 20 DIP outer surface and 12/14/10 DIP inner states. This is a small adaptation, not an exact copy of WinUI's composition scaling. |
| RadioButton | 20 DIP outer circle, 12/14/10 DIP inner states. Color swatches use actual RadioButton semantics rather than pointer-only Borders. |
| ComboBox | Retains the tested Jalium PART_* contract and SelectedItem display path. Uses a stroked chevron rather than the framework's filled triangle; opening/focus does not change layout thickness. |
| Floating surfaces | Solid themed surfaces and strokes are intentional. They are not an acrylic/mica/noise simulation and should not be described as such. |
| Motion | 83 ms brush feedback, 167 ms switch/page transitions and a 200 ms pane transition. Jalium timing/animation APIs are used; curves are not claimed to match WinUI Composition frame-for-frame. The app's reduced-motion option controls its own transitions; native framework popup behavior needs separate verification. |
| Dragging | Mouse retains native DragMove. Touch retains the dedicated TouchDown/Move/Up and per-contact capture path. Do not replace it with a Button, a mouse-only path or unverified WPF APIs. |
| Pen-menu placement | Working-area placement uses the anchor monitor and physical native coordinates after creation; it flips upward or clamps at edges without activating or resizing the menu. Negative monitor origins are included in calculation tests. |

The physical-window interop uses the documented `GetMonitorInfo`, `MonitorFromWindow`
and `SetWindowPos` contracts. Positioning preserves the existing Z order and does not
activate the flyout (`SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE`).
Reference: `https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos`.

## Behavior and state

`AppPreferences` validates and saves theme, reduced motion, mouse-mode toolbar topmost
preference, pen width, pressure, experimental tilt collection, sampling, smoothing and
minimum point spacing. Writes are debounced and use a same-directory temporary file
followed by replacement. Save errors remain visible in Settings; settings are not
represented as saved after a failed write.

The pen width is shared by the settings slider, floating pen menu and the overlay's
drawing attributes. Programmatic pen-menu synchronization does not emit user-change
events. Opening Settings hides the overlay rather than destroying existing strokes.
The toolbar always remains above the drawing canvas while inking/erasing regardless
of the mouse-mode topmost preference. Esc dismisses the pen menu or returns the toolbar
to mouse mode. Tilt is labelled as experimental data collection, not implemented brush tilt.

## Verification record and limits

### Follow-up control completion

- `FluentSettingsRow` changes from text/action columns to stacked rows based on the actual
  available content width (420 DIP for dropdown rows, 320 for switches). Existing controls
  are repositioned, never replaced, preserving focus and bindings.
- `FluentNavigationItem.IsSelected` drives selected/hover/pressed states independently of
  keyboard focus. Navigation and toolbar templates include non-hit-testable focus visuals
  using the source's outer/inner focus brushes. The pane supports Up/Down/Home/End focus movement.
- Toolbar Left/Right/Home/End move focus; Down on the pen opens its menu and focuses the
  selected swatch after the queued Z-order update. Swatch arrows follow the 3-by-3 grid.
  Escape dismisses the menu and restores focus to the pen.
- Switch gesture commits now enter the same OnClick/OnToggle pipeline as keyboard input:
  Click and Command execute once, while a cancelled gesture executes neither. The thumb
  retains pressed feedback for keyboard activation as well as pointer gestures.
- Slider hover/press fills, ComboBox disabled foregrounds and selected-item indicator, and
  separate swatch focus/selection visuals are covered by the shared templates.
- System-color source placeholders are replaced with Jalium's actual SystemColors values
  before application controls are parsed, and refreshed when system settings change.
  This is not a claim of complete high-contrast-theme parity.
- Preference batches update the ink runtime once through a guarded synchronization path;
  the footer reports a pending save until a successful flush clears it.
- Initial Light mode explicitly initializes the framework resource theme, even when the
  shared application palette is already Light, avoiding an OS Dark resource key left behind.
- The expanded integration suite passes **46 checks**. An isolated `--preview` mode runs
  real application windows without writing to the normal user profile.

Follow-up desktop checks used the isolated preview: a mouse grip drag moved the native
toolbar from (210, 630) to (160, 665); actual dropdown clicks switched to Dark; clicking
and then pressing Space toggled reduced motion on and off; a slider drag changed the
pen width from 4 to 8. The pen menu displayed the same 8 px value and mouse palette
selection updated its selection ring. The toolbar could open Settings while in pen mode.
Native programmatic resizing was also visually inspected at the narrow width: compact
navigation and manual pane expansion both rendered, with dropdown/switch actions stacking
below their descriptions when necessary. Keyboard Down moved navigation focus without
changing the selected page, and the pen's Down shortcut opened the palette with focus.
These observations do not certify physical touch or native non-client border-drag resizing.
After the initial-theme correction, a fresh isolated Light preview was opened again:
the Settings caption buttons rendered dark against the Light title bar. Final Debug
integration and Release application builds completed with zero warnings and errors;
the 46-check suite passed. The existing annotation session was left untouched.

During this implementation:

- The application rendered its toolbar and settings in both Light and Dark on the
  connected desktop (175% display scaling). Settings card spacing and text wrapping
  were visually inspected, not inferred from compilation.
- Real mouse clicking and Space toggling were checked on the switch; real slider
  dragging changed the displayed pen width. Theme dropdown selection updated existing
  surfaces and foregrounds without rebuilding the window.
- `tools/UiSmoke` ran **27 passing checks** after the initial implementation, including
  the corrected content-root-based compact resize and synthetic touch capture lifecycle.
- Builds and `git diff --check` should be repeated after subsequent changes.

### Navigation indicator motion correction

The per-item opacity swaps have been removed. `NavigationSelectionIndicator` is a single
non-hit-testable bar in a pane-wide Canvas, including the footer item. Its destination
comes from the selected item's actual layout coordinates, not its ordinal index.
`NavigationIndicatorAnimator` adapts `NavigationView.cpp::PlayIndicatorAnimations` at
reference commit `19e3bdc3c` (lines 1991-1994 and 2177-2235): 200 ms extension followed by
400 ms contraction, with cubic control points (0.9, 0.1)/(1, 0.2) and
(0.1, 0.9)/(0.2, 1). Canvas.Top and Height are animated on the same Jalium UIElement.
This reproduces the moving/stretching bar with Jalium layout animation, not WinUI's
pair of Composition visuals and outgoing-opacity blend.

Retargeting snapshots the displayed geometry before replacing clocks; unchanged
destinations do not restart on LayoutUpdated. Resizing realigns the destination, and
enabling reduced motion completes the active movement. Closing Settings removes clocks.
No toolbar dragging, canvas layering, pen input or preferences logic was changed.

The updated application and smoke project compile. Desktop observations of the new
application include a stretched bar at its departure position followed by the settled
destination, and a correctly aligned indicator after compact-pane switching. Additional
live-clock regression checks were added to UiSmoke, but its executable launch was blocked
by the tool layer in this session, so no new automated pass count is recorded here.

Not certified by those checks: physical touchscreen/stylus behavior, mixed-DPI monitor
crossing on real hardware, accessibility behavior in a screen reader, high-contrast
theme parity, acrylic/mica rendering, or pixel-by-pixel differences against a native
WinUI reference application. Do not turn a successful build or a synthetic event test
into a claim of those additional validations.
