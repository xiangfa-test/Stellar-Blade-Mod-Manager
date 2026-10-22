using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SBModManager.ViewModels;

/// <summary>
/// ObservableCollection that can replace its whole content with a single Reset notification,
/// avoiding one CollectionChanged per item when a large mod list is rebuilt.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();

        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
