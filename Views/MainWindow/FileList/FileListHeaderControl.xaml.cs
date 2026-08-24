using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer.Views.MainWindow.FileList
{
    public sealed partial class FileListHeaderControl : UserControl
    {
        public event Action<bool>? SelectAllRequested;
        public event RoutedEventHandler? SortByNameRequested;
        public event RoutedEventHandler? SortByDateRequested;
        public event RoutedEventHandler? SortByTypeRequested;
        public event RoutedEventHandler? SortBySizeRequested;

        public event PointerEventHandler? ResizeHandleNamePointerPressed;
        public event PointerEventHandler? ResizeHandleDatePointerPressed;
        public event PointerEventHandler? ResizeHandleTypePointerPressed;
        public event PointerEventHandler? ResizeHandleSizePointerPressed;

        public event PointerEventHandler? ResizeHandlePointerMoved;
        public event PointerEventHandler? ResizeHandlePointerReleased;
        public event PointerEventHandler? ResizeHandlePointerCaptureLost;

        public event DoubleTappedEventHandler? ResizeHandleNameDoubleTapped;
        public event DoubleTappedEventHandler? ResizeHandleDateDoubleTapped;
        public event DoubleTappedEventHandler? ResizeHandleTypeDoubleTapped;
        public event DoubleTappedEventHandler? ResizeHandleSizeDoubleTapped;

        public FileListHeaderControl()
        {
            this.InitializeComponent();
        }

        public FrameworkElement ContainerName => HeaderNameContainer;
        public FrameworkElement ContainerDate => HeaderDateContainer;
        public FrameworkElement ContainerType => HeaderTypeContainer;
        public FrameworkElement ContainerSize => HeaderSizeContainer;

        public UIElement HandleName => ResizeHandleName;
        public UIElement HandleDate => ResizeHandleDate;
        public UIElement HandleType => ResizeHandleType;
        public UIElement HandleSize => ResizeHandleSize;

        public void SetColumnWidths(double name, double date, double type, double size)
        {
            HeaderNameContainer.Width = name;
            HeaderDateContainer.Width = date;
            HeaderTypeContainer.Width = type;
            HeaderSizeContainer.Width = size;
        }

        public void UpdateHeaderForRecycleBin(bool isRecycleBin)
        {
            if (BtnHeaderDate != null)
            {
                BtnHeaderDate.Content = isRecycleBin ? "元の場所" : "更新日時";
            }
            if (BtnHeaderType != null)
            {
                BtnHeaderType.Content = isRecycleBin ? "削除日時" : "種類";
            }
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = SelectAllCheckBox.IsChecked == true;
            SelectAllRequested?.Invoke(isChecked);
        }

        public void UpdateSelectAllCheckBox(int selectedCount, int totalCount)
        {
            if (totalCount == 0)
            {
                SelectAllCheckBox.IsChecked = false;
                SelectAllCheckBox.IsEnabled = false;
            }
            else
            {
                SelectAllCheckBox.IsEnabled = true;
                if (selectedCount == 0)
                {
                    SelectAllCheckBox.IsChecked = false;
                }
                else if (selectedCount >= totalCount)
                {
                    SelectAllCheckBox.IsChecked = true;
                }
                else
                {
                    SelectAllCheckBox.IsChecked = null; // 中間状態 (Indeterminate)
                }
            }
        }

        private void SortByName_Click(object sender, RoutedEventArgs e) => SortByNameRequested?.Invoke(sender, e);
        private void SortByDate_Click(object sender, RoutedEventArgs e) => SortByDateRequested?.Invoke(sender, e);
        private void SortByType_Click(object sender, RoutedEventArgs e) => SortByTypeRequested?.Invoke(sender, e);
        private void SortBySize_Click(object sender, RoutedEventArgs e) => SortBySizeRequested?.Invoke(sender, e);

        private void ResizeHandleName_PointerPressed(object sender, PointerRoutedEventArgs e) => ResizeHandleNamePointerPressed?.Invoke(sender, e);
        private void ResizeHandleDate_PointerPressed(object sender, PointerRoutedEventArgs e) => ResizeHandleDatePointerPressed?.Invoke(sender, e);
        private void ResizeHandleType_PointerPressed(object sender, PointerRoutedEventArgs e) => ResizeHandleTypePointerPressed?.Invoke(sender, e);
        private void ResizeHandleSize_PointerPressed(object sender, PointerRoutedEventArgs e) => ResizeHandleSizePointerPressed?.Invoke(sender, e);

        private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e) => ResizeHandlePointerMoved?.Invoke(sender, e);
        private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e) => ResizeHandlePointerReleased?.Invoke(sender, e);
        private void ResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => ResizeHandlePointerCaptureLost?.Invoke(sender, e);

        private void ResizeHandleName_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResizeHandleNameDoubleTapped?.Invoke(sender, e);
        private void ResizeHandleDate_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResizeHandleDateDoubleTapped?.Invoke(sender, e);
        private void ResizeHandleType_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResizeHandleTypeDoubleTapped?.Invoke(sender, e);
        private void ResizeHandleSize_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResizeHandleSizeDoubleTapped?.Invoke(sender, e);
    }
}
