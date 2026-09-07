using System.Collections.ObjectModel;

namespace DevTools.Services;

public static class SelectionRetention
{
    // A refresh may replace row objects. Keep the user's identity, never select a different row.
    public static T? Find<T>(IEnumerable<T> visibleRows, long? selectedId, Func<T, long> getId) where T : class =>
        selectedId.HasValue ? visibleRows.FirstOrDefault(row => getId(row) == selectedId.Value) : null;

    public static void Synchronize<T>(ObservableCollection<T> visibleRows, IReadOnlyList<T> desiredRows) where T : class
    {
        for (var index = 0; index < desiredRows.Count; index++)
        {
            var existing = visibleRows.IndexOf(desiredRows[index]);
            if (existing < 0) visibleRows.Insert(index, desiredRows[index]);
            else if (existing != index) visibleRows.Move(existing, index);
        }
        while (visibleRows.Count > desiredRows.Count) visibleRows.RemoveAt(visibleRows.Count - 1);
    }
}
