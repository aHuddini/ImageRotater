using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Builds the game context-menu entries.
    //
    // Split out of the plugin entry point because it is pure declaration: the
    // backgrounds and covers sections are the same five commands differing only
    // by ArtworkKind, so listing them twice by hand invited them to drift.
    public static class GameMenuBuilder
    {
        public static IEnumerable<GameMenuItem> Build(
            IEnumerable<Game> games, ImageMenuHandler handler)
        {
            var selected = games?.ToList() ?? new List<Game>();

            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                // Captured per iteration: the lambdas below outlive the loop,
                // and closing over the loop variable would give every menu item
                // the last kind.
                ArtworkKind current = kind;
                string section = current == ArtworkKind.Cover
                    ? "ImageRotater|Covers"
                    : "ImageRotater|Backgrounds";

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    // "Artwork", not "images": the picker takes video too, and
                    // a separate "Add video" item would be the same command
                    // behind a narrower filter - with the wrong choice hiding
                    // the files the user came for.
                    Description = "Add artwork files...",
                    Action = a => handler.AddImages(selected, current)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    // Source-agnostic: the dialog offers SteamGridDB and web
                    // search as tabs, so naming one of them here would be
                    // wrong the moment the user switches.
                    Description = "Search images online...",
                    Action = a => handler.BrowseSteamGridDb(selected.FirstOrDefault(), current)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = "Download from SteamGridDB (automatic)",
                    Action = a => handler.DownloadFromSteamGridDb(selected, current)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = "Open folder",
                    Action = a => handler.OpenImageFolder(selected.FirstOrDefault(), current)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = "Remove all",
                    Action = a => handler.ClearImages(selected, current)
                };
            }

            // At the ImageRotater root, not under Backgrounds or Covers: a
            // fragmented video shows as a black tile wherever it is used, and
            // the user chasing one should not have to guess which kind it was.
            yield return new GameMenuItem
            {
                MenuSection = "ImageRotater",
                Description = "Repair videos (fix black tiles)",
                Action = a => handler.RepairVideos(selected)
            };
        }
    }
}
