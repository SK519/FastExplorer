using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer.Views.MainWindow.StatusBar
{
    public sealed partial class StatusBarControl : UserControl
    {
        public event RoutedEventHandler? DetailsViewRequested;
        public event RoutedEventHandler? IconsViewRequested;
        public event RoutedEventHandler? ZoomOutRequested;
        public event RoutedEventHandler? ZoomInRequested;

        public StatusBarControl()
        {
            this.InitializeComponent();
        }

        public string StatusText
        {
            get => StatusTextBlock.Text;
            set => StatusTextBlock.Text = value;
        }

        private void StatusBtnDetailsView_Click(object sender, RoutedEventArgs e) => DetailsViewRequested?.Invoke(sender, e);
        private void StatusBtnIconsView_Click(object sender, RoutedEventArgs e) => IconsViewRequested?.Invoke(sender, e);
        private void StatusBtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke(sender, e);
        private void StatusBtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomInRequested?.Invoke(sender, e);
    }
}
