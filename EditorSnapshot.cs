namespace CellEditor;

public sealed record EditorSnapshot(
    string? FilePath,
    string? FileName,
    int Vertices,
    int Triangles,
    int Shapes,
    string? Error,
    string? Cell = null)
{
    public static EditorSnapshot Empty { get; } = new(null, null, 0, 0, 0, null);
}

public sealed class EditorSession
{
    readonly object gate = new();
    EditorSnapshot snapshot = EditorSnapshot.Empty;

    public EditorSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public void Set(EditorSnapshot snapshot)
    {
        lock (gate)
        {
            this.snapshot = snapshot;
        }
    }
}
