using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Renders an animated preview in a web view.
    //
    // WPF's MediaElement cannot do this job. Setting its Source succeeds and it
    // then dies with a NullReferenceException inside MediaPlayerState while
    // rendering, taking the containing window down - reproduced outside
    // Playnite entirely, for a remote URL and a local file alike, so it is the
    // control and not the media.
    //
    // A web view also plays what MediaElement never could: WebM and animated
    // WebP, which is most of what SteamGridDB serves. That turns the ffmpeg
    // conversion from a requirement into an optimisation.
    //
    // Same package PlayGif uses, and the two-phase startup is its pattern too:
    // the environment can be created before the control is in a visual tree,
    // the core cannot.
    public class PreviewRenderer
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly string _userDataFolder;

        private CoreWebView2Environment _environment;
        private WebView2 _view;
        private bool _ready;

        public PreviewRenderer(string pluginUserDataPath)
        {
            _userDataFolder = Path.Combine(
                pluginUserDataPath ?? Path.GetTempPath(), "WebView2Data");
        }

        public WebView2 View
        {
            get { return _view; }
        }

        // Whether the runtime is present. False means every animated preview
        // falls back to a still frame, which is worth saying on screen rather
        // than leaving a blank panel.
        public bool IsAvailable
        {
            get { return _ready; }
        }

        public WebView2 CreateControl()
        {
            if (_view != null)
            {
                return _view;
            }

            _view = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                Visibility = Visibility.Collapsed
            };

            return _view;
        }

        // Must run AFTER the control is in a visual tree.
        public async Task<bool> InitialiseAsync()
        {
            if (_ready || _view == null)
            {
                return _ready;
            }

            try
            {
                Directory.CreateDirectory(_userDataFolder);

                if (_environment == null)
                {
                    _environment = await CoreWebView2Environment
                        .CreateAsync(null, _userDataFolder)
                        .ConfigureAwait(true);
                }

                // Completion is taken from the EVENT rather than only from the
                // returned task. EnsureCoreWebView2Async needs the UI thread to
                // keep pumping to finish, so awaiting it is fine but blocking
                // on it deadlocks - and the event carries the failure reason,
                // which the task does not.
                var completed = new TaskCompletionSource<bool>();

                EventHandler<CoreWebView2InitializationCompletedEventArgs> onDone = null;

                onDone = (s, e) =>
                {
                    _view.CoreWebView2InitializationCompleted -= onDone;

                    if (!e.IsSuccess)
                    {
                        Logger.Warn(
                            "ImageRotater: WebView2 would not start - "
                            + (e.InitializationException == null
                                ? "no detail"
                                : e.InitializationException.Message));
                    }

                    completed.TrySetResult(e.IsSuccess);
                };

                _view.CoreWebView2InitializationCompleted += onDone;

                await _view.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);

                if (!await completed.Task.ConfigureAwait(true))
                {
                    return false;
                }

                // Nothing here loads a page or takes input, so the browser
                // furniture is only a way for a stray click to navigate the
                // preview somewhere unexpected.
                CoreWebView2Settings settings = _view.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;

                _ready = true;
            }
            catch (Exception ex)
            {
                // Missing runtime is the usual reason, and it is not fatal -
                // stills still preview through WPF's own imaging.
                Logger.Warn(ex, "ImageRotater: WebView2 is unavailable, so animated previews are off");
                _ready = false;
            }

            return _ready;
        }

        // Shows one file, looping and muted.
        public void Show(string url, bool isVideo)
        {
            if (!_ready || string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                _view.CoreWebView2.NavigateToString(BuildPage(url, isVideo));
                _view.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not show the preview");
            }
        }

        public void Clear()
        {
            if (_view == null)
            {
                return;
            }

            _view.Visibility = Visibility.Collapsed;

            if (!_ready)
            {
                return;
            }

            try
            {
                // Navigated away rather than merely hidden: a hidden video keeps
                // decoding, which is the cost being avoided.
                _view.CoreWebView2.NavigateToString(BuildPage(null, false));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not clear the preview");
            }
        }

        // A page holding one centred, looping, muted element.
        //
        // object-fit: contain does what Stretch="Uniform" would: the whole
        // thing is visible at its own aspect ratio, letterboxed rather than
        // cropped or squashed.
        private static string BuildPage(string url, bool isVideo)
        {
            const string Style =
                "<style>html,body{margin:0;height:100%;background:transparent;"
                + "display:flex;align-items:center;justify-content:center;overflow:hidden}"
                + "video,img{max-width:100%;max-height:100%;object-fit:contain}</style>";

            if (string.IsNullOrEmpty(url))
            {
                return "<html><head>" + Style + "</head><body></body></html>";
            }

            string escaped = Escape(url);

            // playsinline and muted together are what let it start without a
            // user gesture - an autoplay video with sound is blocked.
            string element = isVideo
                ? "<video src=\"" + escaped + "\" autoplay loop muted playsinline></video>"
                : "<img src=\"" + escaped + "\">";

            return "<html><head>" + Style + "</head><body>" + element + "</body></html>";
        }

        // The URL goes into an HTML attribute, and these come off the web.
        private static string Escape(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        public void Dispose()
        {
            try
            {
                if (_view != null)
                {
                    _view.Dispose();
                    _view = null;
                }
            }
            catch (Exception)
            {
            }

            _ready = false;
        }
    }
}
