using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UniversalMediaOS.WPF.Helpers
{
    public class ObservableRangeCollection<T> : ObservableCollection<T>
    {
        public ObservableRangeCollection() : base() { }

        public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

        public void ReplaceRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new System.ArgumentNullException(nameof(collection));
            CheckReentrancy();

            // Copy input collection to avoid self-reference enumeration crashes
            var list = new List<T>(collection);

            Items.Clear();
            foreach (var item in list)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new System.ArgumentNullException(nameof(collection));
            CheckReentrancy();

            int startIndex = Count;
            var list = new List<T>(collection);

            foreach (var item in list)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, list, startIndex));
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new System.Action(() => base.OnPropertyChanged(e)));
            }
            else
            {
                base.OnPropertyChanged(e);
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new System.Action(() => base.OnCollectionChanged(e)));
            }
            else
            {
                base.OnCollectionChanged(e);
            }
        }
    }
}
