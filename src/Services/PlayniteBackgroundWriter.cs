using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Writes the chosen image into Playnite's own Game.BackgroundImage field.
    //
    // Why this mode exists: the theme element only renders where a theme
    // explicitly places <ContentControl x:Name="ImageRotater_Background" />.
    // In every other theme the plugin loads, the menus work, and nothing ever
    // appears. Writing Playnite's own field works in EVERY theme with no theme
    // support at all.
    //
    // The trade: this mutates the user's library data instead of overlaying it,
    // and rendering is then Playnite's, so the decode-sizing work does not
    // apply in this mode. Because it mutates library data, every original value
    // is recorded before the first change so the whole thing can be undone.
    public class PlayniteBackgroundWriter
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // What Load/Save persist. Kept as one object so both maps are written
        // together and cannot drift apart across a crash.
        private class WriterState
        {
            public Dictionary<string, string> Originals { get; set; }
            public Dictionary<string, string> Imported { get; set; }

            // Artwork ids this plugin wrote, so a restart can still tell them
            // from the user's own art. See _written.
            public List<string> Written { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly string _backupPath;

        // gameId -> the BackgroundImage value the game had before ImageRotater
        // first touched it. An empty string means "the game genuinely had none",
        // which must be preserved as distinct from "never recorded".
        private Dictionary<string, string> _originals = new Dictionary<string, string>();

        // "gameId|sourcePath" -> the id Playnite gave that file when we imported
        // it. Persisted so a restart does not re-import everything: AddFile
        // copies unconditionally, so without this the store would grow by one
        // copy per rotation, forever.
        private Dictionary<string, string> _imported = new Dictionary<string, string>();

        // Reads the plugin's selection counter, so a queued write can tell
        // whether the user moved on again while it waited.
        //
        // A callback rather than a reference to the plugin, so the writer stays
        // testable without the Playnite API and the dependency points one way.
        // Null means "never stale", which is what tests and any caller that
        // does not care about selection get.
        public Func<int> SelectionGeneration { get; set; }

        // Every artwork id this plugin has written, so "is this the user's own
        // artwork or something we put there?" can be answered exactly.
        //
        // A path test cannot answer it. AddFile IMPORTS a copy into Playnite's
        // store, so after one rotation the game's artwork id resolves inside
        // Playnite's folder rather than ours - and the preserver, which skips
        // artwork it recognises as ours, recognised none of it. It therefore
        // copied our own rotation back in as if it were the user's original,
        // adding a candidate per restart and, for an animated pick, quietly
        // giving rotation a still to land on instead.
        //
        // PERSISTED, and an earlier version of this comment argued the opposite
        // on reasoning that was simply wrong. It claimed the ids were transient
        // because "the current value is rewritten before any preserve can run".
        //
        // It is not. On restart the set starts empty while Game.CoverImage
        // still holds an id THIS PLUGIN wrote last session, and Preserve runs
        // on the first selection - before any rotation rewrites anything. So it
        // saw our own artwork, failed to recognise it, and copied it in as a
        // fresh "original_" candidate. One per restart, and for a game whose
        // pick was animated that quietly handed rotation a still frame of its
        // own GIF to alternate with.
        //
        // Bounded by pruning on save rather than by forgetting: only ids still
        // referenced by a game survive, so this cannot grow without limit.
        private HashSet<string> _written =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void NoteWritten(string artworkId)
        {
            if (string.IsNullOrEmpty(artworkId))
            {
                return;
            }

            _written.Add(artworkId);
            Save();
        }

        // True when this artwork id is one we wrote rather than the user's own.
        public bool WroteArtworkId(string artworkId)
        {
            return !string.IsNullOrEmpty(artworkId) && _written.Contains(artworkId);
        }

        // Kept so restore can find the preserved copy of a game's original
        // artwork when the recorded id no longer resolves.
        private readonly string _imagesRoot;

        public PlayniteBackgroundWriter(IPlayniteAPI api, string pluginUserDataPath)
        {
            _api = api;
            _backupPath = Path.Combine(pluginUserDataPath ?? string.Empty, "original-backgrounds.json");
            _imagesRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "Images");
            Load();
        }

        // True when an artwork id still points at a real file.
        //
        // Playnite reclaims unreferenced library files, so an id recorded
        // before the plugin replaced the artwork can be dead by the time anyone
        // restores it.
        private bool ArtworkIdResolves(string artworkId)
        {
            try
            {
                string full = _api?.Database?.GetFullFilePath(artworkId);
                return !string.IsNullOrEmpty(full) && File.Exists(full);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Puts the preserved copy of a game's original artwork back into
        // Playnite's store and returns its new id, or null when there is no
        // copy to import.
        //
        // OriginalArtPreserver writes these as "original_*" in the game's own
        // folder the first time the plugin touches it, precisely so the user's
        // artwork survives Playnite reclaiming the file it replaced.
        private string ReimportPreservedOriginal(Game game, ArtworkKind kind)
        {
            try
            {
                string folder = Path.Combine(
                    _imagesRoot,
                    game.Id.ToString(),
                    kind == ArtworkKind.Cover ? "covers" : "backgrounds");

                if (!Directory.Exists(folder))
                {
                    return null;
                }

                foreach (string file in Directory.GetFiles(folder, "original_*"))
                {
                    string id = ImportFile(file, game.Id);

                    if (!string.IsNullOrEmpty(id))
                    {
                        Logger.Info(
                            $"ImageRotater: restored \"{game.Name}\" {kind} from the preserved copy - "
                            + "the original Playnite file had been reclaimed");
                        return id;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not re-import the preserved {kind} for \"{game.Name}\"");
                return null;
            }
        }

        public int BackedUpCount
        {
            get { return _originals.Count; }
        }

        // Points a game's background at the given file. No-ops when the value is
        // already correct, so this can be called on every selection without
        // writing to the database each time.
        public bool SetBackground(Game game, string imagePath)
        {
            return SetArtwork(game, imagePath, ArtworkKind.Background);
        }

        // Writes either Playnite field. Covers and backgrounds share the whole
        // mechanism - import the file, swap the id, delete the file we
        // replaced, remember the original - so the only difference is which
        // property is touched.
        // virtual so tests can count writes without standing up the whole
        // Playnite API. Nothing in the plugin overrides it.
        public virtual bool SetArtwork(Game game, string imagePath, ArtworkKind kind)
        {
            if (game == null || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return false;
            }

            try
            {
                RememberOriginal(game, kind);

                // A FRESH import every rotation, deliberately.
                //
                // Fullscreen themes bind CoverImageObjectCached, which resolves
                // through Playnite's ImageSourceManager - a decoded-bitmap cache
                // keyed on the image id string. Reusing an id therefore returns
                // the previously decoded bitmap and the artwork never visibly
                // changes in Fullscreen, even though the database was updated.
                // Desktop binds the uncached variant, which is why it worked
                // there and not here. Plugins cannot evict that cache: the SDK
                // exposes no access to it, only AddFile/RemoveFile.
                //
                // A new id per rotation guarantees a cache miss. The previous
                // copy is deleted immediately below, so this stays bounded at
                // one transient extra copy rather than accumulating.
                string previousId = GetCurrent(game, kind);

                string newId = ImportFile(imagePath, game.Id);
                if (string.IsNullOrEmpty(newId))
                {
                    return false;
                }

                NoteWritten(newId);


                // On the UI thread, deliberately.
                //
                // The property change has to raise a binding notification that
                // reaches the library grid's tiles. Mutating from the thread
                // the selection event happened to arrive on left the grid
                // showing its previously rendered cover until something forced
                // the tiles to be rebuilt - switching grid modes, which is what
                // made this look like "the grid never updates". The details
                // view re-reads on demand, so it always looked correct.
                // Captured before the write is queued, compared when it runs.
                int generation = SelectionGeneration != null ? SelectionGeneration() : 0;

                bool committed = true;

                InvokeOnUi(() =>
                {
                    // Compared HERE, inside the dispatcher callback, because the
                    // queue is the delay - checking before queuing would prove
                    // nothing.
                    //
                    // Backgrounds rotate for the game being LEFT, so this write
                    // is always one selection behind by design. Unequal means at
                    // least one MORE selection arrived while it waited, which
                    // makes it two or more behind - and Playnite's background
                    // element would render a game the user has scrolled well
                    // past.
                    //
                    // Covers are exempt: they rotate for the game arrived at, so
                    // a late cover write still belongs to a game the user chose.
                    if (kind == ArtworkKind.Background &&
                        SelectionGeneration != null &&
                        SelectionGeneration() != generation)
                    {
                        committed = false;
                        return;
                    }

                    SetCurrent(game, kind, newId);
                    _api.Database.Games.Update(game);
                });

                if (!committed)
                {
                    // Nothing was written, so the file imported above is
                    // unreferenced. Drop it rather than leaving an orphan in
                    // Playnite's store.
                    DeleteReplacedCopy(game, kind, newId);
                    return false;
                }

                // Only after the write has committed. Deleting before this
                // point is what produced the intermittent blank artwork: the
                // file vanished while it was still the game's referenced value.
                DeleteReplacedCopy(game, kind, previousId);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not set {kind} for {game.Name}");
                return false;
            }
        }

        // Puts every touched game back the way it was, and forgets the backup.
        public int RestoreAll()
        {
            int restored = 0;

            foreach (KeyValuePair<string, string> entry in _originals.ToList())
            {
                Guid gameId;
                if (!TryParseKey(entry.Key, out gameId))
                {
                    continue;
                }

                try
                {
                    Game game = _api.Database.Games.Get(gameId);
                    if (game == null)
                    {
                        // Game was removed from the library; nothing to restore.
                        continue;
                    }

                    ArtworkKind kind = KindFromKey(entry.Key);
                    string current = GetCurrent(game, kind);

                    // Empty means the game originally had no artwork of this
                    // kind, so null is the correct value to put back.
                    string restoreTo = string.IsNullOrEmpty(entry.Value) ? null : entry.Value;

                    // The recorded id may no longer resolve.
                    //
                    // Once Game.CoverImage points at plugin artwork the original
                    // is unreferenced, and Playnite's own library maintenance is
                    // free to reclaim it. Writing that dead id back gives the
                    // game a reference to nothing, which renders as missing
                    // artwork - the user then has to re-add it by hand, which is
                    // exactly what this whole mechanism exists to prevent.
                    //
                    // OriginalArtPreserver keeps a copy in the plugin's own
                    // folder for precisely this case. Re-import it rather than
                    // hand back an id that resolves to nothing.
                    if (restoreTo != null && !ArtworkIdResolves(restoreTo))
                    {
                        restoreTo = ReimportPreservedOriginal(game, kind) ?? restoreTo;
                    }

                    // On the UI thread for the same reason as SetArtwork: a
                    // restore that does not notify leaves the grid showing
                    // plugin artwork that is no longer in the database.
                    string finalValue = restoreTo;

                    InvokeOnUi(() =>
                    {
                        SetCurrent(game, kind, finalValue);
                        _api.Database.Games.Update(game);
                    });

                    // Restore is the one place plugin-written files are cleaned
                    // up, since rotation deliberately leaves them behind. Only
                    // remove what we put there, and never a file the game still
                    // uses for its other artwork.
                    if (!string.IsNullOrEmpty(current) &&
                        !string.Equals(current, entry.Value, StringComparison.OrdinalIgnoreCase) &&
                        IsSafeToDelete(game, kind, current))
                    {
                        _api.Database.RemoveFile(current);
                    }

                    restored++;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"ImageRotater: could not restore artwork for {entry.Key}");
                }
            }

            _originals.Clear();

            // The imported copies have just been deleted, so their remembered
            // ids no longer resolve. Keeping them would make a later rotation
            // reuse an id pointing at nothing.
            _imported.Clear();

            Save();

            return restored;
        }

        // Removes the copy a rotation just replaced, keeping the store bounded
        // now that every rotation imports afresh.
        //
        // Safe because Playnite's store is output only - candidates come solely
        // from the plugin's own folder, so nothing being deleted here can be a
        // rotation candidate. When the store was also a source, this delete
        // removed files a concurrent rotation had already chosen, which is what
        // produced the intermittent blank and stretched artwork.
        //
        // Still called only after Games.Update has committed the new id, so the
        // file being removed is no longer referenced.
        private void DeleteReplacedCopy(Game game, ArtworkKind kind, string previousId)
        {
            if (string.IsNullOrEmpty(previousId))
            {
                return;
            }

            try
            {
                // Never the user's own artwork. IsSafeToDelete refuses anything
                // recorded as an original for either kind, or still used by the
                // game's other artwork slots.
                if (!IsSafeToDelete(game, kind, previousId))
                {
                    return;
                }

                _api.Database.RemoveFile(previousId);
            }
            catch (Exception ex)
            {
                // A failed delete costs one leftover file, which RestoreAll
                // clears. Not worth failing the rotation over.
                Logger.Warn(ex, $"ImageRotater: could not remove the replaced {kind} copy for \"{game.Name}\"");
            }
        }

        // Runs the action on Playnite's UI thread, synchronously.
        //
        // Synchronous on purpose: SetArtwork deletes the replaced file straight
        // after this returns, and that delete must not overtake the commit.
        //
        // Falls back to running inline when there is no dispatcher - unit tests
        // and any non-UI host - so the writer stays usable without one.
        //
        // virtual so a test can stand in for the dispatcher and simulate the
        // user moving on WHILE a write is queued. That gap is the entire bug
        // this guards against, and it cannot be reproduced by calling
        // SetArtwork normally.
        protected virtual void InvokeOnUi(Action action)
        {
            System.Windows.Threading.Dispatcher dispatcher = null;

            try
            {
                dispatcher = _api?.MainView?.UIDispatcher;
            }
            catch (Exception)
            {
                // Some hosts throw rather than return null when there is no
                // main view. Either way the inline path below is correct.
            }

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        private void RememberOriginal(Game game, ArtworkKind kind)
        {
            string key = MakeKey(game.Id, kind);

            // Only the FIRST value is kept. Recording again on a later rotation
            // would overwrite the user's real original with plugin artwork,
            // making restore a no-op.
            if (_originals.ContainsKey(key))
            {
                return;
            }

            _originals[key] = GetCurrent(game, kind) ?? string.Empty;
            Save();
        }

        // Backgrounds and covers are backed up independently, so the key
        // carries the kind. Backgrounds keep the bare-guid form used before
        // covers existed, so an existing backup file still restores.
        private static string MakeKey(Guid gameId, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover
                ? "cover:" + gameId
                : gameId.ToString();
        }

        private static bool TryParseKey(string key, out Guid gameId)
        {
            gameId = Guid.Empty;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            string raw = key.StartsWith("cover:", StringComparison.OrdinalIgnoreCase)
                ? key.Substring("cover:".Length)
                : key;

            return Guid.TryParse(raw, out gameId);
        }

        private static ArtworkKind KindFromKey(string key)
        {
            return !string.IsNullOrEmpty(key) &&
                   key.StartsWith("cover:", StringComparison.OrdinalIgnoreCase)
                ? ArtworkKind.Cover
                : ArtworkKind.Background;
        }

        private static string GetCurrent(Game game, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? game.CoverImage : game.BackgroundImage;
        }

        // Imports a file into Playnite's own store and returns its id.
        //
        // virtual for the same reason as the two below: without it, reaching
        // any of the commit logic in a test needs a live Playnite database.
        protected virtual string ImportFile(string imagePath, Guid gameId)
        {
            return _api.Database.AddFile(imagePath, gameId);
        }

        // virtual so a test can observe whether a write actually reached the
        // game - the difference between "suppressed as stale" and "committed"
        // is otherwise invisible without a live Playnite database.
        protected virtual void SetCurrent(Game game, ArtworkKind kind, string value)
        {
            if (kind == ArtworkKind.Cover)
            {
                game.CoverImage = value;
            }
            else
            {
                game.BackgroundImage = value;
            }
        }

        // Never delete a file that is a recorded original, or that Playnite is
        // still using elsewhere on the game. Both kinds' originals are checked,
        // not just this one's: a game whose cover and background were the same
        // file would otherwise lose the other kind's way back.
        private bool IsSafeToDelete(Game game, ArtworkKind kind, string fileId)
        {
            foreach (ArtworkKind other in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                string original;
                if (_originals.TryGetValue(MakeKey(game.Id, other), out original) &&
                    string.Equals(original, fileId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Still referenced by the game's other artwork slots.
            string otherSlot = kind == ArtworkKind.Cover ? game.BackgroundImage : game.CoverImage;

            return !string.Equals(fileId, otherSlot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fileId, game.Icon, StringComparison.OrdinalIgnoreCase);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_backupPath))
                {
                    return;
                }

                string json = File.ReadAllText(_backupPath);

                // Files written before the imported-id map existed are a bare
                // dictionary of originals. Read either shape so an existing
                // backup keeps working and restore is not silently lost.
                var state = JsonConvert.DeserializeObject<WriterState>(json);
                if (state != null && state.Originals != null)
                {
                    _originals = state.Originals;
                    _imported = state.Imported ?? new Dictionary<string, string>();
                    _written = state.Written != null
                        ? new HashSet<string>(state.Written, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                _originals = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                _imported = new Dictionary<string, string>();
                _written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // A corrupt backup must not stop the plugin loading, but it does
                // mean restore is no longer possible - say so loudly.
                Logger.Error(ex, "ImageRotater: could not read the original-background backup. Restore will not be available.");
                _originals = new Dictionary<string, string>();
                _imported = new Dictionary<string, string>();
                _written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_backupPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Temp-then-move: an interrupted write must not leave a corrupt
                // backup, because that is the user's only way back.
                var state = new WriterState
                {
                    Originals = _originals,
                    Imported = _imported,
                    Written = new List<string>(_written)
                };

                string temp = _backupPath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));

                if (File.Exists(_backupPath))
                {
                    File.Delete(_backupPath);
                }

                File.Move(temp, _backupPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not save the original-background backup");
            }
        }
    }
}
