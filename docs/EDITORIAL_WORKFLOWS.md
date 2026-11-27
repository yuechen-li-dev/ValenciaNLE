# Editorial workflows

`Valencia.Cli.exe workflow list` reports the registered operations, analysis requirements, examples and template identities. Use `workflow info creator` to inspect an entry. Settings accept JSON or a single typed TSON literal.

```ts
const $value: EditorialSettings = {
    start: "1s",
    end: "3s",
    text: "Chapter one",
    subtitle: "The setup"
};
```

```powershell
Valencia.Cli.exe workflow prepare creator-title --project recording.valencia --settings title.obj.ts --plan-out title-plan.json
Valencia.Cli.exe plan review title-plan.json --project recording.valencia --at 508032000000 --output title-review.png
Valencia.Cli.exe plan apply title-plan.json --project recording.valencia --save-as edited.valencia
```

Use `editorial_workflow` with `action: "prepare"` and the active project ID and exact revision. Its response carries a candidate handle and canonical plan hash. Existing `observe` and `agent_plan` provide review, comparison, discard and explicit commit. A sequence position accepts frame, timecode, elapsed time or marker references. B-roll's source start is relative to the selected source stream. Imported observations and ranked creator ranges use container-relative source time.

To compose up to eight entries, choose `action: "compose"` and pass `steps: [{workflow,settings}, ...]`. Each step sees the preceding candidate. Resolve later timeline positions in that staged coordinate space. A failed composition leaves the live document unchanged. A successful commit is one undoable transaction. The Script picker can preview a saved `*.editorial.json` prepare or compose request on the current desktop session.

The [Copeland workflow source](../src/Valencia.Scripting/Editorial/editorial-workflows.ts) defines reusable title, caption, vertical and picture-layout parts. Its specialized functions invoke typed CLR primitives. Local trusted scripts can obtain canonical plans using `api.BuildEditorialPlan(id, settingsJson)` and use `api.ApplyPlan(plan)` explicitly. The CLR backend currently works with boolean specialization for these presets. Runtime overrides still use typed settings. Arbitrary script execution remains a local trusted workflow.

## Provider requests

- Run `start_analysis` with `kind: "ocr"` and an optional `ocr` object. Supply `VALENCIA_TESSERACT_EXE` as an absolute path and use its sibling `tessdata` or set `VALENCIA_TESSDATA_DIR`. Use `analysis_status` for progress, then `footage_query(kind:"ocr", artifacts:[key])`.
- Use `footage_query(kind:"topics", artifacts:[transcriptKey])` for a deterministic topic outline. The cue is a lexical boundary with supporting words and a nearby excerpt.
- Use `editorial_workflow(action:"submit-observations")` with a source asset, stream, provider, version, configuration, evidence type and timestamped observations. The resulting key can be read through `footage_query`. For subject following, supply a `subjects` artifact to `make-short` as `observations`; choose `subject` when the desired track is known.

## Creator handoff

Start or reuse transcript, audio and optional scene artifacts for the selected recording. Pass the keys, topic phrases or reviewed source ranges, a target duration and a Shorts count to `creator`. Review alternatives through the existing proposal tools, checkpoint the base, and explicitly apply the selected candidate. Save As records the edited revision. Queue named profiles through `render_queue`, then inspect the emitted lint. The optional Dominatus runner can hold the revision, artifacts and candidate IDs across these stages. [M3b export contracts](EXPORT_PROFILES.md)

Ranked ranges accept a topic, score and reason. `requiredRanges` must all fit the target and minimum separation. `excludedRanges` reject any overlapping candidate. `diversifyTopics` spreads the ranked selections across topic groups. Set `sourceOrder: false` to keep selection order in the edit. Too little eligible material produces a duration diagnostic.

Reproduce the controlled demonstration with `python -B scripts/M3cProof.py --bundle <extracted bundle> --fixture artifacts/local/m2d --cache artifacts/local/.valencia-analysis --report <new.json>`. The report includes the exact workspace, settings, paired review, canonical plan, encoded outputs and independent checks.
