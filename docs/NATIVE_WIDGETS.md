# Shared native Machina components

The new interactive component layer lives in the sibling Copeland repository's
`src/Machina.UI/Machina.Standard/Widgets`, not in Valencia's editing engine.
Its contracts, behavior and coverage are documented in Copeland at
`docs/Machina.UI/native-widgets.md`.

Valencia adds only `NativeWidgetGallery.cs` and two command-line entry points:

```powershell
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/local/native-widgets-host -m:1 -nr:false -p:UseSharedCompilation=false
./artifacts/local/native-widgets-host/Valencia.App.exe --widget-gallery
./artifacts/local/native-widgets-host/Valencia.App.exe --widgets-smoke --output artifacts/local/native-widgets-smoke
```

The native gallery is a working consumer using Aurelian Vulkan, native font
measurement, the existing presenter and the shared native input adapter. It
includes checkboxes, switches, toggles, text/number inputs, slider, radio groups,
tabs, disclosures, toolbar, select, combobox, menu, dialogs, popover, tooltip and
scroll area. It is deliberately separate from the existing editor layout; this
increment does not migrate every legacy editor control or redesign its theme.

The shared API returns typed proposals. Application code applies them to its
model and rebuilds the view. In Valencia, semantic edits must still pass through
the editing engine. Keep pointer capture, focus, text drafts, popup state and
slider previews transient. `Preview` does not create an editing undo entry;
`Commit` can create one; `Cancel` restores the gesture's original value.

`WidgetSession` and the input adapter belong to the native window thread.
Callbacks queue input only. Poll/handle input, apply proposals, reprepare changed
declarations, then submit the prepared presentation to the existing presenter.
Supply `NativeUiText.Instance` in lowering options so layout and caret geometry
use the same font measurements. Idle frames reuse prepared presentation.

The shared library preserves existing `StandardUI` APIs, so the editor continues
to function while subsequent frontend changes adopt the interactive controls.
The component set is not complete Base UI parity: native OS accessibility/IME,
several advanced families, virtualized lists and scrollbar thumb dragging are
not implemented. See the shared coverage matrix before choosing a widget.

Current-source evidence is in [validation/native-widgets.json](validation/native-widgets.json).
The gallery checks real Vulkan rendering and native WM_CHAR transport plus
scripted semantic interactions. Physical input and OS accessibility are not
claimed as qualified. The editor/export smoke independently verifies that
existing playback/export still works with the updated shared libraries.
