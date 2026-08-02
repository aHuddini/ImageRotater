using System;
// ObservableObject is declared in System.Collections.Generic inside
// Playnite.SDK.dll, not in the Playnite.SDK namespace.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // A filter option plus whether the user ticked it. Options are rebuilt from
    // each result set rather than kept in a static list, which is what stops
    // them duplicating or offering values no result has.
    public class FilterOption : ObservableObject
    {
        private bool isChecked;

        public string Value { get; set; }
        public string Label { get; set; }

        public bool IsChecked
        {
            get => isChecked;
            set { isChecked = value; OnPropertyChanged(); }
        }
    }

    // A named aspect ratio with its dimensions nested underneath. Ticking the
    // group ticks all of its dimensions, which is what makes "give me square
    // covers" one click rather than several.
    public class AspectGroupOption : ObservableObject
    {
        private bool isChecked;

        public string Label { get; set; }
        public int TotalCount { get; set; }
        public ObservableCollection<FilterOption> Dimensions { get; }
            = new ObservableCollection<FilterOption>();

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                isChecked = value;
                OnPropertyChanged();

                foreach (FilterOption dimension in Dimensions)
                {
                    dimension.IsChecked = value;
                }
            }
        }
    }

    // Drives the SteamGridDB search dialog. Kept free of WPF types beyond
    // ObservableObject so the search/filter flow is testable without a window.
    public class SteamGridDbSearchViewModel : ObservableObject
    {
        private readonly ISteamGridDbClient _client;

        private List<SteamGridDbArtwork> _allResults = new List<SteamGridDbArtwork>();

        private string _status = string.Empty;
        private bool _isBusy;

        public ObservableCollection<SteamGridDbArtwork> Results { get; }
            = new ObservableCollection<SteamGridDbArtwork>();

        public ObservableCollection<FilterOption> DimensionOptions { get; }
            = new ObservableCollection<FilterOption>();

        // Dimensions grouped under a named aspect ratio, so the user picks
        // "2:3 - Steam Vertical" rather than reading raw numbers.
        public ObservableCollection<AspectGroupOption> AspectGroups { get; }
            = new ObservableCollection<AspectGroupOption>();

        public ObservableCollection<FilterOption> StyleOptions { get; }
            = new ObservableCollection<FilterOption>();

        public ArtworkFilterState Filter { get; } = new ArtworkFilterState();

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public SteamGridDbSearchViewModel(ISteamGridDbClient client)
        {
            _client = client;
        }

        // Resolves a game name to artwork. Returns false when nothing was found
        // or the request failed, with Status carrying the reason.
        // Runs a free-text web image search instead of querying SteamGridDB.
        //
        // Results land in the same list and carry the same shape, so filtering,
        // the checkboxes and the download loop all work unchanged. Web results
        // have no curated style, so the style filter will show a single "web"
        // entry for them - that is honest rather than hiding the difference.
        public async Task<bool> SearchWebAsync(string query, WebImageSearch search)
        {
            if (search == null || !search.IsAvailable)
            {
                Status = "Web search is not available.";
                return false;
            }

            IsBusy = true;
            try
            {
                // Only the browsing goes off-thread. Everything below touches
                // ObservableCollections that are bound to the UI, and WPF
                // refuses collection changes from any thread but the
                // dispatcher - running the whole method off-thread threw
                // NotSupportedException from RebuildFilterOptions.
                List<SteamGridDbArtwork> found =
                    await Task.Run(() => search.Search(query).ToList()).ConfigureAwait(true);

                _allResults = found;
                RebuildFilterOptions();
                ApplyFilter();

                if (_allResults.Count == 0)
                {
                    Status = $"No images found for \"{query}\".";
                    return false;
                }

                Status = $"{Results.Count} of {_allResults.Count} shown for \"{query}\".";
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> SearchAsync(string gameName, SteamGridDbArtworkType type)
        {
            if (_client == null || !_client.IsConfigured)
            {
                Status = "No SteamGridDB API key configured. Add one in ImageRotater settings.";
                return false;
            }

            IsBusy = true;
            try
            {
                SteamGridDbResult<List<SteamGridDbGame>> games =
                    await _client.SearchGamesAsync(gameName).ConfigureAwait(true);

                if (!games.Success)
                {
                    Status = games.Error;
                    return false;
                }

                if (games.Data == null || games.Data.Count == 0)
                {
                    Status = $"SteamGridDB has no match for \"{gameName}\".";
                    return false;
                }

                // First match: the autocomplete endpoint returns the closest
                // name match first, which is right for a per-game action.
                SteamGridDbResult<List<SteamGridDbArtwork>> artwork =
                    await _client.GetArtworkAsync(games.Data[0].Id, type).ConfigureAwait(true);

                if (!artwork.Success)
                {
                    Status = artwork.Error;
                    return false;
                }

                _allResults = artwork.Data ?? new List<SteamGridDbArtwork>();
                RebuildFilterOptions();
                ApplyFilter();

                // Both names, deliberately. SteamGridDB resolves the query to
                // its own title, so searching "persona 3" and "Persona 3
                // Reload" can land on the same game and return identical
                // results - which looks like the search did not run at all.
                // Showing what was matched, and what it was matched from,
                // makes that visible instead of mysterious.
                string matched = games.Data[0].Name;
                string via = string.Equals(matched, gameName, StringComparison.OrdinalIgnoreCase)
                    ? $"\"{matched}\""
                    : $"\"{matched}\" (matched from \"{gameName}\")";

                if (_allResults.Count == 0)
                {
                    Status = $"No artwork found for {via}.";
                    return false;
                }

                Status = $"{Results.Count} of {_allResults.Count} shown for {via}.";
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Options come from the current results, so they cannot duplicate and
        // cannot offer a value that would filter everything away.
        private void RebuildFilterOptions()
        {
            DimensionOptions.Clear();
            AspectGroups.Clear();

            // Grouped by aspect ratio, with the flat list kept in step so
            // ApplyFilter has one place to read ticked dimensions from.
            foreach (AspectGrouping grouping in AspectGroup.Build(_allResults))
            {
                var group = new AspectGroupOption
                {
                    Label = grouping.Label,
                    TotalCount = grouping.TotalCount
                };

                foreach (DimensionCount dimension in grouping.Dimensions)
                {
                    var option = new FilterOption
                    {
                        Value = dimension.Dimensions,
                        Label = $"{dimension.Dimensions} ({dimension.Count})"
                    };

                    group.Dimensions.Add(option);
                    DimensionOptions.Add(option);
                }

                AspectGroups.Add(group);
            }

            StyleOptions.Clear();
            foreach (string style in ArtworkFilter.AvailableStyles(_allResults))
            {
                StyleOptions.Add(new FilterOption { Value = style, Label = style });
            }
        }

        // Everything ticked, across ALL results rather than the visible ones.
        //
        // Filtering rebuilds Results, so reading the visible list would silently
        // drop a tick the user made before narrowing the filter - they would ask
        // for five images and get three with no explanation.
        public IReadOnlyList<SteamGridDbArtwork> SelectedArtwork()
        {
            return _allResults.Where(a => a.IsSelected).ToList();
        }

        public void ApplyFilter()
        {
            Filter.Dimensions.Clear();
            foreach (FilterOption option in DimensionOptions.Where(o => o.IsChecked))
            {
                Filter.Dimensions.Add(option.Value);
            }

            Filter.Styles.Clear();
            foreach (FilterOption option in StyleOptions.Where(o => o.IsChecked))
            {
                Filter.Styles.Add(option.Value);
            }

            Results.Clear();
            foreach (SteamGridDbArtwork item in ArtworkFilter.Apply(_allResults, Filter))
            {
                Results.Add(item);
            }

            if (_allResults.Count > 0)
            {
                Status = $"{Results.Count} of {_allResults.Count} shown.";
            }
        }
    }
}
