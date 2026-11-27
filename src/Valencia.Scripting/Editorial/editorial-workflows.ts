using Valencia.Scripting;

// These are creative presets. Static arguments specialize the authored design.
template <static LowerThird: boolean>
function TitlePart(edit: EditorialInputs): void {
    if (LowerThird) {
        edit.Title("lowerThird", "slideUp", 0.73, 0.085, 4294967295L, 269489663L);
    } else {
        edit.Title("card", "fade", 0.46, 0.15, 4294967295L, 269489663L);
    }
}

template <static Compact: boolean = false>
function CaptionPart(edit: EditorialInputs): void {
    if (Compact) {
        edit.Caption(38, 0.80, 4294967295L, 1289748479L);
    } else {
        edit.Caption(46, 0.82, 4294967295L, 4294902015L);
    }
}

template <static Compact: boolean = false>
function VerticalPart(edit: EditorialInputs): void {
    if (Compact) {
        edit.Short(540, 960, 0.78);
    } else {
        edit.Short(1080, 1920, 0.78);
    }
}

template <static SplitScreen: boolean>
function LayoutPart(edit: EditorialInputs): void {
    if (SplitScreen) {
        edit.Layout(true);
    } else {
        edit.Layout(false);
    }
}

function tightenPauses(api: ScriptApi): string {
    api.EditorialInput().Tighten();
    return "ready";
}

function removeFillers(api: ScriptApi): string {
    api.EditorialInput().Fillers(["um", "uh", "erm", "like", "well", "you know"]);
    return "ready";
}

function topicCut(api: ScriptApi): string {
    api.EditorialInput().Topic();
    return "ready";
}

function jCut(api: ScriptApi): string {
    api.EditorialInput().SplitEdit(true);
    return "ready";
}

function lCut(api: ScriptApi): string {
    api.EditorialInput().SplitEdit(false);
    return "ready";
}

function insertBroll(api: ScriptApi): string {
    api.EditorialInput().Broll();
    return "ready";
}

function duckSpeech(api: ScriptApi): string {
    api.EditorialInput().Duck();
    return "ready";
}

function makeShort(api: ScriptApi): string {
    VerticalPart(api.EditorialInput());
    return "ready";
}

function highlightReel(api: ScriptApi): string {
    const edit = api.EditorialInput();
    edit.Creator(true);
    CaptionPart(edit);
    return "ready";
}

function creatorWorkflow(api: ScriptApi): string {
    const edit = api.EditorialInput();
    edit.Creator(false);
    CaptionPart(edit);
    return "ready";
}

function creatorTitle(api: ScriptApi): string {
    TitlePart<false>(api.EditorialInput());
    return "ready";
}

function lowerThird(api: ScriptApi): string {
    TitlePart<true>(api.EditorialInput());
    return "ready";
}

function wordHighlight(api: ScriptApi): string {
    CaptionPart(api.EditorialInput());
    return "ready";
}

function tutorialShort(api: ScriptApi): string {
    const edit = api.EditorialInput();
    VerticalPart<true>(edit);
    CaptionPart<true>(edit);
    return "ready";
}

function facecam(api: ScriptApi): string {
    LayoutPart<false>(api.EditorialInput());
    return "ready";
}

function splitScreen(api: ScriptApi): string {
    LayoutPart<true>(api.EditorialInput());
    return "ready";
}
