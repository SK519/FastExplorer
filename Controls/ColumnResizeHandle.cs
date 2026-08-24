using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FastExplorer.Controls
{
    public sealed class ColumnResizeHandle : Grid
    {
        public ColumnResizeHandle()
        {
            this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
            this.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            Canvas.SetZIndex(this, 100);
        }
    }
}
