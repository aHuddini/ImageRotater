using System.Collections.Generic;

namespace ImageRotater.Controls
{
    // What a theme binds when it hosts CoverImageControl.
    //
    // The control exists to carry this, not to render. Aniki's author
    // describes the pattern for BackgroundChanger: keep the plugin control
    // hidden, bind its injected Content.Source, and draw the image through the
    // theme's own element so the theme keeps control of layout, clipping and
    // transitions.
    //
    // ImagePath is a PATH, deliberately, not a decoded bitmap. The theme binds
    // it with IsAsync=True so decoding happens off the layout thread - which is
    // the difference between a control that survives in a Fullscreen grid tile
    // and one that takes Playnite down when dozens of tiles realise at once.
    //
    // ObservableObject lives in System.Collections.Generic inside
    // Playnite.SDK.dll, which is why that namespace is imported here.
    public class CoverImageDataContext : ObservableObject
    {
        private string imagePath = string.Empty;

        public string ImagePath
        {
            get => imagePath;
            set
            {
                if (imagePath == value)
                {
                    return;
                }

                imagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
            }
        }

        // Lets a theme collapse its own artwork only when there is something to
        // replace it with, without needing a converter.
        public bool HasImage => !string.IsNullOrEmpty(imagePath);
    }
}
