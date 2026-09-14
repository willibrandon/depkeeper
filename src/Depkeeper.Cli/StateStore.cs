using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Persists maintenance state atomically without storing credentials or transcripts.
/// </summary>
internal sealed class StateStore
{
    private readonly string _path;

    /// <summary>
    /// Loads an existing state file or creates an empty state.
    /// </summary>
    /// <param name="path">The state file path.</param>
    internal StateStore(string path)
    {
        _path = Path.GetFullPath(path);
        State = File.Exists(_path)
            ? JsonSerializer.Deserialize(File.ReadAllText(_path), JsonContext.Default.MaintenanceState)
                ?? throw new InvalidDataException("Invalid maintenance state.")
            : new MaintenanceState(1, new Dictionary<string, AttemptState>(StringComparer.Ordinal));
        if (State.Version != 1 || State.PullRequests is null) throw new InvalidDataException("Unsupported maintenance state.");
    }

    /// <summary>
    /// Gets the loaded maintenance state.
    /// </summary>
    internal MaintenanceState State { get; private set; }

    /// <summary>
    /// Rotates the next sweep after the repository that last consumed repair capacity.
    /// </summary>
    /// <param name="index">The next repository index.</param>
    internal void SetNextRepository(int index)
    {
        State = State with { NextRepository = index };
        Save();
    }

    /// <summary>
    /// Records a state transition before subsequent external operations.
    /// </summary>
    /// <param name="key">The pull request identity.</param>
    /// <param name="value">The new state.</param>
    internal void Set(string key, AttemptState value)
    {
        State.PullRequests[key] = value;
        Save();
    }

    /// <summary>
    /// Writes state using an atomic file replacement.
    /// </summary>
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(State, JsonContext.Default.MaintenanceState));
        File.Move(temporary, _path, true);
    }
}
