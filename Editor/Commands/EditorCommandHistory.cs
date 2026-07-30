// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.RHI;

namespace Engine.Editor.Commands;

/// <summary>Represents one reversible editor scene mutation.</summary>
public interface IEditorCommand
{
    /// <summary>Gets the label displayed by undo and redo controls.</summary>
    string Name { get; }

    /// <summary>Restores state preceding the mutation.</summary>
    void Undo();

    /// <summary>Reapplies the mutation.</summary>
    void Redo();
}

/// <summary>Maintains bounded undo and redo stacks for scene mutations.</summary>
public sealed partial class EditorCommandHistory : ObservableObject
{
    private const int Capacity = 256;
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    /// <summary>Occurs after history or scene state changes.</summary>
    public event Action? Changed;

    /// <summary>Gets whether an undo operation is available.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets whether a redo operation is available.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Gets the current undo menu label.</summary>
    public string UndoLabel =>
        CanUndo ? $"Undo {_undo.Peek().Name}" : "Undo";

    /// <summary>Gets the current redo menu label.</summary>
    public string RedoLabel =>
        CanRedo ? $"Redo {_redo.Peek().Name}" : "Redo";

    /// <summary>Records a mutation that has already been applied.</summary>
    public void Record(IEditorCommand command)
    {
        _undo.Push(command);
        _redo.Clear();
        TrimUndo();
        NotifyChanged();
    }

    /// <summary>Undoes the most recently recorded mutation.</summary>
    public void Undo()
    {
        if (!_undo.TryPop(out IEditorCommand? command))
            return;

        command.Undo();
        _redo.Push(command);
        NotifyChanged();
    }

    /// <summary>Redoes the most recently undone mutation.</summary>
    public void Redo()
    {
        if (!_redo.TryPop(out IEditorCommand? command))
            return;

        command.Redo();
        _undo.Push(command);
        NotifyChanged();
    }

    /// <summary>Clears history when the active scene changes.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        NotifyChanged();
    }

    private void TrimUndo()
    {
        if (_undo.Count <= Capacity)
            return;

        IEditorCommand[] commands = _undo.ToArray();
        _undo.Clear();
        for (int index = Capacity - 1; index >= 0; --index)
            _undo.Push(commands[index]);
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        Changed?.Invoke();
    }
}

/// <summary>Restores component state before and after an entity edit.</summary>
public sealed class EntityStateCommand : IEditorCommand
{
    private readonly EcsWorld _world;
    private readonly EntitySnapshot _before;
    private readonly EntitySnapshot _after;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Creates an entity-state command.</summary>
    public EntityStateCommand(
        string name,
        EcsWorld world,
        EntitySnapshot before,
        EntitySnapshot after)
    {
        Name = name;
        _world = world;
        _before = before;
        _after = after;
    }

    /// <inheritdoc />
    public void Undo() => _before.Apply(_world);

    /// <inheritdoc />
    public void Redo() => _after.Apply(_world);
}

/// <summary>Reverses creation of one scene entity.</summary>
public sealed class EntityCreatedCommand : IEditorCommand
{
    private readonly EcsWorld _world;
    private readonly EntitySnapshot _created;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Creates an entity-creation command.</summary>
    public EntityCreatedCommand(
        string name,
        EcsWorld world,
        EntitySnapshot created)
    {
        Name = name;
        _world = world;
        _created = created;
    }

    /// <inheritdoc />
    public void Undo() => _world.DeleteEntity(_created.EntityId);

    /// <inheritdoc />
    public void Redo() => _created.Restore(_world);
}

/// <summary>Reverses deletion of one scene entity.</summary>
public sealed class EntityDeletedCommand : IEditorCommand
{
    private readonly EcsWorld _world;
    private readonly EntitySnapshot _deleted;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Creates an entity-deletion command.</summary>
    public EntityDeletedCommand(
        string name,
        EcsWorld world,
        EntitySnapshot deleted)
    {
        Name = name;
        _world = world;
        _deleted = deleted;
    }

    /// <inheritdoc />
    public void Undo() => _deleted.Restore(_world);

    /// <inheritdoc />
    public void Redo() => _world.DeleteEntity(_deleted.EntityId);
}
