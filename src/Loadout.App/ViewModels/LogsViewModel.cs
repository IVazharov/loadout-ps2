using System.Collections.ObjectModel;
using System.Globalization;
using Loadout.Core;

namespace Loadout.App;

public sealed class LogsViewModel : ObservableObject
{
    public LogsViewModel(OperationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Lines = new ObservableCollection<LogEntry>(log.Recent.Select(line => new LogEntry(line)));
        Lines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsEmpty));
        };
        log.Added += line =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Lines.Insert(0, new LogEntry(line));
                return;
            }

            dispatcher.Invoke(() => Lines.Insert(0, new LogEntry(line)));
        };
    }

    public ObservableCollection<LogEntry> Lines { get; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>The page header's summary, such as "24 ENTRIES".</summary>
    public string Summary => Lines.Count switch
    {
        0 => "",
        1 => "1 ENTRY",
        _ => Lines.Count.ToString(CultureInfo.CurrentCulture) + " ENTRIES",
    };
}

/// <summary>
/// One line in Logs. A class, not a string, so two lines with the same text are still two items
/// to select and copy.
/// </summary>
public sealed class LogEntry(string text)
{
    public string Text { get; } = text;

    public override string ToString() => Text;
}
