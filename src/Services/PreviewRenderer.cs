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
        private readonly string _hostFolder;

        // The origin the YouTube player is embedded FROM.
        //
        // It has to be a real https origin, and a dotted name rather than a
        // bare word so it parses as a host everywhere that reads it back.
        // Nothing is served from the internet under it -- the folder below is
        // local -- but to YouTube it is an ordinary website embedding a video,
        // which is the only arrangement its player will start in.
        private const string PreviewHost = "imagerotater.preview";

        private CoreWebView2Environment _environment;
        private WebView2 _view;
        private bool _ready;

        public PreviewRenderer(string pluginUserDataPath)
        {
            string root = pluginUserDataPath ?? Path.GetTempPath();

            _userDataFolder = Path.Combine(root, "WebView2Data");
            _hostFolder = Path.Combine(root, "PreviewHost");
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

                // The browser furniture is only a way for a stray click to
                // navigate the preview somewhere unexpected. The YouTube player
                // is the one page loaded here that does take input, and it
                // wants only its own transport controls -- a context menu over
                // it offers to open YouTube proper, inside a 320px panel with
                // no way back.
                /* The virtual host, mapped BEFORE anything navigates.

                   A mapping added after a Navigate does not apply to it, and the
                   navigation fails with ERR_NAME_NOT_RESOLVED -- the host simply
                   does not exist yet as far as that request is concerned.

                   This exists because of YouTube error 153. The player checks
                   the Referer on its embed request and refuses to start without
                   one, and there are two ways to have no Referer: navigate to
                   /embed/<id> at the top level, which sends none at all, and
                   embed it from a NavigateToString document, whose origin is
                   opaque and so sends none either. Both were tried. Serving the
                   embedding page from a mapped host gives the iframe request a
                   real https origin to quote, which is all the check wants. */
                WriteHostPage();

                _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    PreviewHost, _hostFolder, CoreWebView2HostResourceAccessKind.Allow);

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

        // YouTube is played by YouTube, in its own player, embedded from a
        // page of ours served under a real origin.
        //
        // Not Show(): that builds a page holding <video src="...">, and a
        // YouTube watch URL is an HTML page rather than a media file. A <video>
        // pointed at HTML decodes nothing, raises an error event no one is
        // listening for, and renders an empty box -- so the preview looked
        // broken while reporting perfect health. Every other source hands the
        // tag a real .webm or .mp4; this one never can, because YouTube does
        // not publish one at a stable URL.
        public void ShowYouTube(string videoId)
        {
            if (!_ready || string.IsNullOrEmpty(videoId))
            {
                return;
            }

            try
            {
                _view.CoreWebView2.Navigate(YouTubePreviewUrl(videoId));
                _view.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not show the YouTube preview");
            }
        }

        // The id rides in the FRAGMENT rather than the query.
        //
        // A fragment never leaves the browser, so the page is one static file
        // that every preview reuses -- the alternative is rewriting an HTML file
        // to disk per click, which is a file write on the UI thread to change
        // eleven characters.
        public static string YouTubePreviewUrl(string videoId)
        {
            return "https://" + PreviewHost + "/youtube.html#"
                + Uri.EscapeDataString(videoId ?? string.Empty);
        }

        // The embedding page.
        //
        // It goes through the IFrame Player API rather than a bare <iframe> for
        // one reason: onError. A plain iframe that YouTube refuses to play is
        // cross-origin and silent, which is the failure this whole area already
        // shipped once -- a blank rectangle and no way to tell whether the
        // preview was loading, broken, or simply not allowed. Roughly one video
        // in ten disables embedding, and that has to read as the uploader's
        // choice rather than as a bug.
        //
        // Muted because an autoplaying video with sound is blocked outright,
        // and a player that will not start looks exactly like one that is
        // broken.
        public static string HostPageHtml
        {
            get { return HostPage; }
        }

        private const string HostPage = @"<!doctype html>
<html><head><meta charset=""utf-8"">
<style>
html,body{margin:0;height:100%;background:#000;overflow:hidden;
  display:flex;align-items:center;justify-content:center;
  font:13px ""Segoe UI"",sans-serif;color:#cfd6e4}
#player,iframe{width:100%;height:100%;border:0}
#fallback{display:none;flex-direction:column;align-items:center;gap:10px;
  padding:16px;text-align:center;line-height:1.45}
#poster{max-width:100%;border-radius:6px}
</style></head>
<body>
<div id=""player""></div>
<div id=""fallback""><img id=""poster"" alt=""""><span id=""why""></span></div>
<script>
var id = (location.hash || '').replace(/^#/, '');

function fail(message){
  document.getElementById('player').style.display = 'none';
  if (id) document.getElementById('poster').src =
    'https://i.ytimg.com/vi/' + encodeURIComponent(id) + '/hqdefault.jpg';
  document.getElementById('why').textContent = message;
  document.getElementById('fallback').style.display = 'flex';
}

// Said in the uploader's terms where it IS the uploader's doing. ""Error 150""
// reads as a fault in this plugin; ""the uploader does not allow it"" does not.
function reason(code){
  if (code === 101 || code === 150)
    return 'The uploader does not allow this video to play outside YouTube.';
  if (code === 100) return 'That video has been removed or made private.';
  if (code === 2)   return 'YouTube did not recognise that video id.';
  if (code === 5)   return 'YouTube could not play this video here.';
  return 'YouTube would not play this video here (error ' + code + ').';
}

function onYouTubeIframeAPIReady(){
  if (!id){ fail('No video was given to preview.'); return; }
  // Kept on window, and the last state with it. A cross-origin iframe cannot be
  // inspected from outside, so this reference is the only way anything -- a
  // diagnostic, a test harness -- can ask whether the player actually started
  // rather than merely appeared.
  window.__player = new YT.Player('player', {
    videoId: id,
    playerVars: { autoplay: 1, mute: 1, playsinline: 1, rel: 0, modestbranding: 1 },
    events: {
      onError: function(e){ fail(reason(e.data)); },
      onStateChange: function(e){ window.__state = e.data; }
    }
  });
}

// No network, no player, and otherwise no explanation either.
setTimeout(function(){ if (!window.YT) fail('Could not reach YouTube.'); }, 8000);
</script>
<script src=""https://www.youtube.com/iframe_api""></script>
</body></html>";

        // Rewritten only when it differs, so a preview does not touch the disk
        // on every startup for a file that has not changed.
        private void WriteHostPage()
        {
            try
            {
                Directory.CreateDirectory(_hostFolder);

                string path = Path.Combine(_hostFolder, "youtube.html");

                if (!File.Exists(path) || File.ReadAllText(path) != HostPage)
                {
                    File.WriteAllText(path, HostPage);
                }
            }
            catch (Exception ex)
            {
                // Not fatal on its own: the navigation below will fail visibly
                // rather than silently, and stills are unaffected.
                Logger.Warn(ex, "ImageRotater: could not write the preview host page");
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
