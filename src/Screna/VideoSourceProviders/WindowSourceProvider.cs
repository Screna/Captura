using System.Collections.Generic;
using Screna;

namespace Captura.Models
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class WindowSourceProvider : VideoSourceProviderBase
    {
        readonly WindowPickerItem _windowPickerItem;

        public WindowSourceProvider(LanguageManager Loc, WindowPickerItem WindowPickerItem) : base(Loc)
        {
            _windowPickerItem = WindowPickerItem;
        }

        public override IEnumerator<IVideoItem> GetEnumerator()
        {
            yield return _windowPickerItem;

            yield return WindowItem.TaskBar;

            foreach (var win in Window.EnumerateVisible())
                yield return new WindowItem(win);
        }

        public override string Name => Loc.Window;

        public override string Description =>
            @"Record a specific window.
The video is of the initial size of the window.";
    }
}