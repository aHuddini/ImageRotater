using System.Windows.Controls;

namespace ImageRotater
{
    // No code-behind beyond construction, deliberately.
    //
    // The external tool paths bind commands and status strings on the view
    // model instead - the pattern FullVid and UniPlaySong already use. An
    // earlier draft here reached into named TextBlocks to set their text and
    // colour by hand, which put logic somewhere untestable and made the view
    // and the model disagree about who owned the state.
    public partial class ImageRotaterSettingsView : UserControl
    {
        public ImageRotaterSettingsView()
        {
            InitializeComponent();
        }
    }
}
