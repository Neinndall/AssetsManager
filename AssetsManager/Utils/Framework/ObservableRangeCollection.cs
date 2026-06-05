using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AssetsManager.Utils.Framework
{
    /// <summary> 
    /// Extension of ObservableCollection to support AddRange and suppress multiple notifications.
    /// This significantly improves UI performance when adding many items at once.
    /// </summary>
    public class ObservableRangeCollection<T> : ObservableCollection<T>
    {
        private bool _isSuppressingNotification = false;

        public ObservableRangeCollection() : base() { }

        public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

        public ObservableRangeCollection(List<T> list) : base(list) { }

        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            var list = new List<T>(collection);
            if (list.Count == 0) return;

            _isSuppressingNotification = true;
            int startIndex = Count;
            try
            {
                foreach (var item in list)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _isSuppressingNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));

            const int BatchSize = 500;
            for (int i = 0; i < list.Count; i += BatchSize)
            {
                int count = Math.Min(BatchSize, list.Count - i);
                var batch = list.GetRange(i, count);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, batch, startIndex + i));
            }
        }

        /// <summary> 
        /// Clears the collection and adds a range of items, raising a single Reset notification.
        /// </summary>
        public void ReplaceRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            _isSuppressingNotification = true;
            try
            {
                Clear();
                foreach (var item in collection)
                {
                    Add(item);
                }
            }
            finally
            {
                _isSuppressingNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_isSuppressingNotification)
            {
                base.OnCollectionChanged(e);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_isSuppressingNotification)
            {
                base.OnPropertyChanged(e);
            }
        }
    }
}
