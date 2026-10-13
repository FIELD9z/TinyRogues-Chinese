# Native layout regression probe

Build `LayoutProbe.csproj` with `-p:GameDir=<game copy>` and install the resulting
DLL in a **separate diagnostic copy** under `BepInEx/plugins/LayoutProbe/`.
The localization plugin and its packs must also be installed in that copy.

Run with `-tinyrogues-layout-probe` to exercise native dialogue controls, a
priced tavern offer, Chinese card sections, and wrapping restoration. The probe
never invokes choice callbacks and blocks SaveManager.Save while armed.
It exits automatically; errors are appended to `layout-error.txt`.

For paragraph regressions, copy `tests/fixtures/layout-regressions.json` to the
probe directory as `layout-corpus.json`, then add `-tinyrogues-layout-corpus`.
Other arrays of `{name,text}` may be used for broader sampling. These test input
strings assess layout, not whether every original game owner is translated.

Validate output with `python tools/check_layout.py --corpus <corpus-layout.jsonl>`
or `--choices <layout.jsonl>`. The latter requires all nine phases.
`offscreen-N.png` renders the native canvas through a temporary camera;
`glyphs-N.png` renders actual TMP glyph meshes and positions without the panel.
These are native UI reproductions, not gameplay screenshots. Regular screenshots
can be black in headless mode and are not verification evidence.
