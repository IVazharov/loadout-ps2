namespace Loadout.Core;

/// <summary>
/// Recent operation results, newest first. Each entry is one line.
/// </summary>
public sealed class OperationLog
{
    public const int Capacity = 200;

    private readonly List<string> _lines = new();

    public event Action<string>? Added;

    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Add(string? message)
    {
        var line = UserFacingText.Describe(message);
        lock (_lines)
        {
            _lines.Insert(0, line);
            if (_lines.Count > Capacity)
            {
                _lines.RemoveAt(_lines.Count - 1);
            }
        }

        Added?.Invoke(line);
    }
}
