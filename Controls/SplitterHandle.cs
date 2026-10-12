using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer.Controls
{
    public class SplitterHandle : Grid
    {
        public SplitterHandle()
        {
            this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
    }
}
