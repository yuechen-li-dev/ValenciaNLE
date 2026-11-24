using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Copeland.TS.Tson;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record SourcePositionDto(string Value, int Numerator, int Denominator, string OffsetTicks)
{
    public SourcePosition ToCore() => new(new(EngineJson.Int64(Value), Numerator, Denominator), new(EngineJson.Int64(OffsetTicks)));
}
public sealed record ClipDto(Guid Id, Guid AssetId, int StreamIndex, string Start, SourcePositionDto SourceIn,
    SourcePositionDto SourceOut, ClipProperties Properties)
{
    public TimelineClip ToCore() => new(Id, AssetId, StreamIndex, new(EngineJson.Int64(Start)), SourceIn.ToCore(), SourceOut.ToCore(), Properties);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(InsertPlan), "insert")]
[JsonDerivedType(typeof(MovePlan), "move")]
[JsonDerivedType(typeof(TrimPlan), "trim")]
[JsonDerivedType(typeof(SplitPlan), "split")]
[JsonDerivedType(typeof(DeletePlan), "delete")]
[JsonDerivedType(typeof(RipplePlan), "rippleDelete")]
[JsonDerivedType(typeof(TrackStatePlan), "trackState")]
[JsonDerivedType(typeof(AddTrackPlan), "addTrack")]
[JsonDerivedType(typeof(CreatorPlan), "creator")]
[JsonDerivedType(typeof(PropertiesPlan), "properties")]
[JsonDerivedType(typeof(ColorPlan), "color")]
[JsonDerivedType(typeof(RegionPlan), "region")]
[JsonDerivedType(typeof(OutputFramingPlan), "outputFraming")]
public abstract record PlanOperation
{
    public abstract EditOperation ToCore(Guid sequenceId);
}
public sealed record InsertPlan(Guid TrackId, ClipDto Clip) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new InsertClip(sequenceId, TrackId, Clip.ToCore());
}
public sealed record MovePlan(Guid ClipId, Guid TrackId, string Start) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new MoveClip(sequenceId, ClipId, TrackId, new(EngineJson.Int64(Start)));
}
public sealed record TrimPlan(Guid ClipId, string Start, SourcePositionDto SourceIn, SourcePositionDto SourceOut) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new TrimClip(sequenceId, ClipId, new(EngineJson.Int64(Start)), SourceIn.ToCore(), SourceOut.ToCore());
}
public sealed record SplitPlan(Guid ClipId, string At, Guid RightClipId) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SplitClip(sequenceId, ClipId, new(EngineJson.Int64(At)), RightClipId);
}
public sealed record DeletePlan(Guid ClipId) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new DeleteClip(sequenceId, ClipId);
}
public sealed record RipplePlan(ImmutableArray<Guid> TrackIds, string Start, string End, ImmutableArray<RippleSplitId> SplitIds) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new RippleDelete(sequenceId, TrackIds, new(EngineJson.Int64(Start)), new(EngineJson.Int64(End)), SplitIds);
}
public sealed record TrackStatePlan(Guid TrackId, TrackState State) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SetTrackState(sequenceId, TrackId, State);
}
public sealed record AddTrackPlan(Guid TrackId, TrackKind Kind, int Order, string Name) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new AddTrack(sequenceId, TrackId, Kind, Order, Name);
}
public sealed record CreatorPlan(CreatorSettings Settings) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SetCreatorSettings(sequenceId, Settings);
}
public sealed record PropertiesPlan(Guid ClipId, ClipProperties Properties) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SetClipProperties(sequenceId, ClipId, Properties);
}
public sealed record ColorPlan(Guid ClipId, ColorPatch Color) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new PatchClipColor(sequenceId, ClipId, Color);
}
public sealed record RegionPlan(Guid ClipId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegionEffect? Region = null) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SetClipRegion(sequenceId, ClipId, Region);
}
public sealed record OutputFramingPlan(Guid ProfileId, string? Name = null, int? Width = null, int? Height = null,
    string? Fit = null, double? FocalX = null, double? FocalY = null) : PlanOperation
{
    public override EditOperation ToCore(Guid sequenceId) => new SetOutputFraming(sequenceId, ProfileId, Name, Width, Height, Fit, FocalX, FocalY);
}
public sealed record SourcePrecondition(Guid AssetId, string Fingerprint);
public sealed record EditPlan(int SchemaVersion, Guid ProjectId, string ProjectRevision, Guid SequenceId,
    ImmutableArray<PlanOperation> Operations, string? Note = null, ImmutableArray<SourcePrecondition>? SourcePreconditions = null);
public sealed record ClipChange(string Kind, Guid ClipId, Guid? BeforeTrack, Guid? AfterTrack, TimelineClip? Before, TimelineClip? After);
public sealed record OutputProfileChange(Guid ProfileId, OutputProfile? Before, OutputProfile? After);
public sealed record PlanResult(int ApiVersion, bool Valid, bool Committed, string ExpectedRevision, string ResultingRevision,
    string CurrentRevision, bool CanCommit, ImmutableArray<Guid> AffectedIds, ImmutableArray<ClipChange> Changes,
    SequenceTime? ResultingDuration, ImmutableArray<string> Diagnostics, ImmutableArray<OutputProfileChange>? OutputProfiles = null);

public static class EditPlanSchema
{
    public static TsonCatalog Catalog { get; } = Load();
    private static TsonCatalog Load()
    {
        using var stream = typeof(EditPlanSchema).Assembly.GetManifestResourceStream("Valencia.Engine.Contracts.edit-plan.obj.ts")!;
        using var reader = new StreamReader(stream);
        var result = TypeProfileCompiler.Compile(reader.ReadToEnd(), "edit-plan.obj.ts", "copeland://valencia/edit-plan/v1");
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        return result.Catalog!;
    }
    public static EditPlan Parse(string source, string? sourcePath = null)
    {
        if (source.Length > 1_048_576) throw new ArgumentException("Edit plan exceeds 1 MiB source limit.");
        JsonElement value;
        if (sourcePath?.EndsWith(".obj.ts", StringComparison.OrdinalIgnoreCase) == true)
            value = TypeProfileCompiler.ReadTypedValue(source, sourcePath, Catalog, "EditPlan");
        else
        {
            using var document = JsonDocument.Parse(source);
            value = document.RootElement.Clone();
        }
        var errors = TypeProfileCompiler.Validate(Catalog, "EditPlan", value);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        var plan = EngineJson.Read<EditPlan>(value.GetRawText());
        if (plan.SchemaVersion != 1 || plan.Operations.IsDefaultOrEmpty || plan.Operations.Length > 500)
            throw new ArgumentException("Plan schema must be 1 with 1..500 operations.");
        _ = EngineJson.Int64(plan.ProjectRevision);
        return plan;
    }
}
