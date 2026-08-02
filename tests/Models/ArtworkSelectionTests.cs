using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Tests.Models
{
    // Selection lives on the artwork model rather than in ListBox.SelectedItems.
    //
    // Applying a filter rebuilds the visible list, so reading the ListBox would
    // silently drop a tick made before the filter narrowed - the user asks for
    // five images and gets three, with nothing on screen explaining why.
    [TestFixture]
    public class ArtworkSelectionTests
    {
        private static SteamGridDbArtwork Art(int id, int width = 1920, int height = 1080)
        {
            return new SteamGridDbArtwork
            {
                Id = id,
                Url = "https://example.invalid/" + id,
                Width = width,
                Height = height,
                Style = "alternate",
                Mime = "image/jpeg"
            };
        }

        [Test]
        public void NothingIsSelectedByDefault()
        {
            Assert.IsFalse(Art(1).IsSelected);
        }

        [Test]
        public void SelectionRaisesPropertyChanged()
        {
            var art = Art(1);
            var raised = new List<string>();
            art.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            art.IsSelected = true;
            CollectionAssert.Contains(raised, nameof(SteamGridDbArtwork.IsSelected),
                "the checkbox binding needs the notification");

            raised.Clear();
            art.IsSelected = true;
            CollectionAssert.DoesNotContain(raised, nameof(SteamGridDbArtwork.IsSelected),
                "an unchanged value should not churn the binding");
        }

        // The reason selection is not read from the visible list: a tick made
        // before filtering must still count once the item is filtered out.
        [Test]
        public void SelectionSurvivesFiltering()
        {
            var wide = Art(1, 1920, 1080);
            var tall = Art(2, 600, 900);
            var all = new List<SteamGridDbArtwork> { wide, tall };

            wide.IsSelected = true;
            tall.IsSelected = true;

            // Narrow to tall images only - the wide one leaves the visible list.
            var filter = new ArtworkFilterState();
            filter.Dimensions = new HashSet<string> { "600x900" };

            var visible = ArtworkFilter.Apply(all, filter);
            CollectionAssert.DoesNotContain(visible, wide, "the wide image should be filtered out");

            // Both remain selected, because selection is on the model.
            Assert.AreEqual(2, all.Count(a => a.IsSelected),
                "a tick made before filtering must still count toward the download");
        }

        [Test]
        public void DeselectingLeavesOthersAlone()
        {
            var a = Art(1);
            var b = Art(2);
            a.IsSelected = true;
            b.IsSelected = true;

            a.IsSelected = false;

            Assert.IsFalse(a.IsSelected);
            Assert.IsTrue(b.IsSelected);
        }
    }
}
