using Valencia.Core.Timeline;

namespace Valencia.Core.Editing;

/// <summary>Serializes commits. Immutable candidates validate before the single commit point.</summary>
public sealed class EditingSession
{
    private readonly object gate = new();
    private readonly int historyLimit;
    private readonly List<ProjectDocument> undo = [];
    private readonly List<ProjectDocument> redo = [];
    private ProjectSnapshot snapshot;

    public EditingSession(ProjectDocument document, int historyLimit = 100) : this(new ProjectSnapshot(document, 0), historyLimit) { }
    public EditingSession(ProjectSnapshot initial, int historyLimit = 100)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentOutOfRangeException.ThrowIfNegative(initial.Revision);
        ArgumentOutOfRangeException.ThrowIfNegative(historyLimit);
        DocumentValidator.Validate(initial.Document);
        snapshot = initial;
        this.historyLimit = historyLimit;
    }
    public ProjectSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public bool CanUndo { get { lock (gate) return undo.Count > 0; } }
    public bool CanRedo { get { lock (gate) return redo.Count > 0; } }

    public EditResult Execute(EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Execute(new EditTransaction(command.Id, command.SchemaVersion, command.ExpectedRevision ?? -1, [command]));
    }

    public EditResult Preview(EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Apply(new EditTransaction(command.Id, command.SchemaVersion, command.ExpectedRevision ?? -1, [command]), commit: false);
    }
    public EditResult Execute(EditTransaction transaction) => Apply(transaction, commit: true);
    public EditResult Preview(EditTransaction transaction) => Apply(transaction, commit: false);
    private EditResult Apply(EditTransaction transaction, bool commit)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (gate)
        {
            var failure = CheckRequest(transaction.Id, transaction.SchemaVersion, transaction.ExpectedRevision);
            if (failure is not null) return failure;
            if (transaction.Commands.IsDefaultOrEmpty) return Fail(transaction.Id, EditError.InvalidCommand, "Transaction is empty.");
            var candidate = snapshot.Document;
            var commandIds = new HashSet<Guid>();
            for (int index = 0; index < transaction.Commands.Length; index++)
            {
                var command = transaction.Commands[index];
                if (command is null || command.Id == Guid.Empty || !commandIds.Add(command.Id) || command.SchemaVersion != EditCommand.CurrentSchemaVersion
                    || command.Operation is null)
                    return Fail(transaction.Id, EditError.InvalidCommand, "Invalid command identity/schema/operation.", index);
                if (command.ExpectedRevision is { } expected && expected != snapshot.Revision)
                    return Fail(transaction.Id, EditError.StaleRevision, "Child command revision must match transaction's starting revision.", index);
                try
                {
                    candidate = EditReducer.Apply(candidate, command.Operation);
                    DocumentValidator.Validate(candidate);
                }
                catch (ArgumentException error) { return Fail(transaction.Id, EditError.InvalidEdit, error.Message, index); }
                catch (OverflowException error) { return Fail(transaction.Id, EditError.Overflow, error.Message, index); }
            }
            if (DocumentSemantics.Equal(candidate, snapshot.Document)) return Fail(transaction.Id, EditError.NoChange, "Transaction makes no semantic change.");
            if (snapshot.Revision == long.MaxValue) return Fail(transaction.Id, EditError.Overflow, "Project revision overflow.");
            if (!commit) return new(true, transaction.Id, snapshot.Revision, new(candidate, snapshot.Revision));
            Push(undo, snapshot.Document);
            redo.Clear();
            snapshot = new(candidate, snapshot.Revision + 1);
            return Ok(transaction.Id);
        }
    }

    public EditResult Undo(SessionRequest request) => Navigate(request, undo, redo, EditError.NothingToUndo);
    public EditResult Redo(SessionRequest request) => Navigate(request, redo, undo, EditError.NothingToRedo);

    private EditResult Navigate(SessionRequest request, List<ProjectDocument> from, List<ProjectDocument> to, EditError empty)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var failure = CheckRequest(request.Id, request.SchemaVersion, request.ExpectedRevision);
            if (failure is not null) return failure;
            if (from.Count == 0) return Fail(request.Id, empty, "History is empty.");
            if (snapshot.Revision == long.MaxValue) return Fail(request.Id, EditError.Overflow, "Project revision overflow.");
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            Push(to, snapshot.Document);
            snapshot = new(target, snapshot.Revision + 1);
            return Ok(request.Id);
        }
    }

    /// <summary>Commit a fully loaded/validated candidate. Existing session revision stays monotonic; history is cleared.</summary>
    public EditResult ReplaceProject(ProjectSnapshot candidate, SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var failure = CheckRequest(request.Id, request.SchemaVersion, request.ExpectedRevision);
            if (failure is not null) return failure;
            try
            {
                ArgumentOutOfRangeException.ThrowIfNegative(candidate.Revision);
                DocumentValidator.Validate(candidate.Document);
            }
            catch (ArgumentException error) { return Fail(request.Id, EditError.InvalidEdit, error.Message); }
            catch (OverflowException error) { return Fail(request.Id, EditError.Overflow, error.Message); }
            if (snapshot.Revision == long.MaxValue) return Fail(request.Id, EditError.Overflow, "Project revision overflow.");
            snapshot = new(candidate.Document, snapshot.Revision + 1);
            undo.Clear(); redo.Clear();
            return Ok(request.Id);
        }
    }

    private EditResult? CheckRequest(Guid id, int schema, long revision)
    {
        if (id == Guid.Empty || schema != EditCommand.CurrentSchemaVersion || revision < 0)
            return Fail(id, EditError.InvalidCommand, "Request requires identity, supported schema and expected revision.");
        return revision != snapshot.Revision ? Fail(id, EditError.StaleRevision, "Project revision is stale.") : null;
    }
    private void Push(List<ProjectDocument> list, ProjectDocument document)
    {
        if (historyLimit == 0) return;
        if (list.Count == historyLimit) list.RemoveAt(0);
        list.Add(document);
    }
    private EditResult Ok(Guid id) => new(true, id, snapshot.Revision, snapshot);
    private EditResult Fail(Guid id, EditError error, string message, int? index = null) => new(false, id, snapshot.Revision, snapshot, error, message, index);
}
