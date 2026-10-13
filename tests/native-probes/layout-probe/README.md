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

## Related controls

For `-tinyrogues-related-layout`, copy `tests/fixtures/layout-regressions.json`
to `layout-corpus.json` and `tests/fixtures/related-tooltips.json` to
`tooltip-corpus.json` beside the probe DLL. This exercises native 3/6/11-choice
lists, three TextBox prefabs including comparison sections, and world-hosted
TooltipBox variants. Definitions are invoked natively; `capture:*` rows use the
recorded complete text in the tooltip control. No choice callback is invoked.

`-tinyrogues-panel-layout` visits all loaded class inspectors, mastery tooltips,
and help-text resources, then verifies Chinese/English/native-spacing reuse.
Help text is assigned through the actual proxies without marking tutorials seen.
Use `-layout-font=PcSenior`, `Renogare`, or `Unifont` for either mode.
`-layout-related-focus` and `-layout-panel-reuse` narrow the corresponding mode.

Validate complete output:

```text
python tools/check_related_layout.py --expected 191 related-layout.jsonl
python tools/check_related_layout.py --expected 154 panel-layout.jsonl
```

The capture records font, missing CJK glyphs, line-box and glyph-quad gaps,
container size, backwards inline advances, and world-space text bounds. Negative
line-box gaps alone do not establish visible collisions. Quad/AABB candidates
also need visual inspection: texture padding and unrelated horizontal positions
can produce conservative positives. Native glyph renders are saved for selected
equipment, tooltip, class, and mastery examples.

Menu and character-panel probes also capture this geometry for active text with
nonzero effective alpha. Those visibility flags do not prove the canvas is drawn
or the text is inside a scrolling viewport; retain that boundary in reports.
