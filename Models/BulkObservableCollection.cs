using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FastExplorer
{
    /// <summary>
    /// 大量アイテムの一括追加・置換時に個別の CollectionChanged イベントを抑制し、
    /// 単一の Reset 通知を発行することで WinUI 仮想化リストの再計算を最小限に抑える高速コレクション。
    /// </summary>
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotification = false;

        public BulkObservableCollection() : base() { }
        public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }
        public BulkObservableCollection(List<T> list) : base(list) { }

        public void AddRange(IEnumerable<T> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            CheckReentrancy();

            int initialCount = Items.Count;
            _suppressNotification = true;
            try
            {
                foreach (var item in items)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
                if (Items.Count != initialCount)
                {
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                    OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                }
            }
        }

        public void ReplaceAll(IEnumerable<T> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            CheckReentrancy();

            if (ReferenceEquals(items, this) || ReferenceEquals(items, Items))
                return;

            var itemList = items as IList<T> ?? new List<T>(items);
            if (Items.Count == 0 && itemList.Count == 0)
                return;

            _suppressNotification = true;
            try
            {
                Items.Clear();
                foreach (var item in itemList)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnCollectionChanged(e);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnPropertyChanged(e);
            }
        }
    }
}
