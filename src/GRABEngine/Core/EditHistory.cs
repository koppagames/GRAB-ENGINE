using System;
using System.Collections.Generic;

namespace GrabEngine.Core;

/// <summary>
/// One complete editor state. The whole level is stored as project JSON so every
/// authoring surface (scene, inspector, scripts, ambience, animation) participates in
/// the same undo stack. Compiled data is JsonIgnore and is rebuilt after a restore.
/// </summary>
public sealed class EditSnapshot
{
    public EditSnapshot(string json, string? selectedId, string label)
    {
        Json = json;
        SelectedId = selectedId;
        Label = label;
    }

    public string Json { get; }
    public string? SelectedId { get; set; }
    public string Label { get; set; }
}

/// <summary>
/// Application-wide undo/redo stack. Continuous gestures (dragging a gizmo, scrubbing
/// an inspector slider, typing a script) pass a coalesce key so the whole gesture is
/// one undo step. Every other edit is its own step.
/// </summary>
public sealed class EditHistory
{
    private const int MaxSteps = 120;
    private readonly List<HistoryStep> _undo = new();
    private readonly List<HistoryStep> _redo = new();

    private string? _coalesceKey;
    private DateTime _lastRecordUtc;

    private sealed class HistoryStep
    {
        public HistoryStep(EditSnapshot snapshot, string label)
        {
            Snapshot = snapshot;
            Label = label;
        }

        public EditSnapshot Snapshot { get; }
        public string Label { get; }
    }

    /// <summary>The clean state the next edit is compared against.</summary>
    public EditSnapshot? Current { get; private set; }

    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(650);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public string? UndoLabel => _undo.Count == 0 ? null : _undo[^1].Label;
    public string? RedoLabel => _redo.Count == 0 ? null : _redo[^1].Label;

    public void Reset(string json, string? selectedId)
    {
        _undo.Clear();
        _redo.Clear();
        _coalesceKey = null;
        _lastRecordUtc = default;
        Current = new EditSnapshot(json, selectedId, "Edit");
    }

    /// <summary>Points the history at the current level without adding a step.</summary>
    public void SyncCurrent(string json, string? selectedId)
    {
        if (Current == null)
        {
            Reset(json, selectedId);
            return;
        }
        Current = new EditSnapshot(json, selectedId, Current.Label);
    }

    public void UpdateSelection(string? selectedId)
    {
        if (Current != null) Current.SelectedId = selectedId;
    }

    /// <summary>
    /// Records a new editor state. Returns true when a new undo step was created;
    /// consecutive calls with the same coalesce key update the current snapshot instead.
    /// </summary>
    public bool Record(string json, string? selectedId, string label, string? coalesceKey = null)
    {
        if (Current == null)
        {
            Reset(json, selectedId);
            return false;
        }

        var now = DateTime.UtcNow;
        bool sameState = string.Equals(Current.Json, json, StringComparison.Ordinal);
        if (sameState)
        {
            Current.SelectedId = selectedId;
            return false;
        }

        bool coalesce = coalesceKey != null &&
                        string.Equals(coalesceKey, _coalesceKey, StringComparison.Ordinal) &&
                        now - _lastRecordUtc <= CoalesceWindow;

        if (coalesce)
        {
            // The existing undo step already points at the state before this gesture.
            // Keep that step, but move Current forward so the next action starts cleanly.
            Current = new EditSnapshot(json, selectedId, label);
        }
        else
        {
            _undo.Add(new HistoryStep(Current, label));
            if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
            _redo.Clear();
            Current = new EditSnapshot(json, selectedId, label);
        }

        _coalesceKey = coalesceKey;
        _lastRecordUtc = now;
        return !coalesce;
    }

    public EditSnapshot? Undo(string currentJson, string? selectedId, out string? label)
    {
        label = null;
        if (_undo.Count == 0) return null;

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new HistoryStep(new EditSnapshot(currentJson, selectedId, step.Label), step.Label));
        if (_redo.Count > MaxSteps) _redo.RemoveAt(0);
        Current = step.Snapshot;
        _coalesceKey = null;
        label = step.Label;
        return step.Snapshot;
    }

    public EditSnapshot? Redo(string currentJson, string? selectedId, out string? label)
    {
        label = null;
        if (_redo.Count == 0) return null;

        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new HistoryStep(new EditSnapshot(currentJson, selectedId, step.Label), step.Label));
        if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        Current = step.Snapshot;
        _coalesceKey = null;
        label = step.Label;
        return step.Snapshot;
    }
}
