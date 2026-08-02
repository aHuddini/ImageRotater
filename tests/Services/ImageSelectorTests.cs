using System;
using System.Collections.Generic;
using NUnit.Framework;
using ImageRotater;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    // Session mode is the fix for the reported bug: artwork changing while the
    // user merely navigates the library. These tests pin that a game's choice
    // survives repeat visits, and that the other two modes still behave.
    [TestFixture]
    public class ImageSelectorTests
    {
        private SessionSelectionCache _cache;
        private ImageSelector _selector;
        private Guid _gameId;
        private List<string> _candidates;

        [SetUp]
        public void SetUp()
        {
            _cache = new SessionSelectionCache();
            _selector = new ImageSelector(new ImagePicker(new Random(1234)), _cache);
            _gameId = Guid.NewGuid();
            _candidates = new List<string> { "a.jpg", "b.jpg", "c.jpg", "d.jpg" };
        }

        [Test]
        public void Session_RepeatVisitsReturnTheSameImage()
        {
            string first = _selector.Select(_gameId, _candidates, null, SelectionMode.Session);

            for (int i = 0; i < 50; i++)
            {
                string again = _selector.Select(_gameId, _candidates, null, SelectionMode.Session);
                Assert.AreEqual(first, again, "session mode re-rolled while browsing");
            }
        }

        [Test]
        public void Session_DifferentGamesGetIndependentChoices()
        {
            var other = Guid.NewGuid();

            string a = _selector.Select(_gameId, _candidates, null, SelectionMode.Session);
            string b = _selector.Select(other, _candidates, null, SelectionMode.Session);

            Assert.AreEqual(a, _selector.Select(_gameId, _candidates, null, SelectionMode.Session));
            Assert.AreEqual(b, _selector.Select(other, _candidates, null, SelectionMode.Session));
        }

        // The remembered path must be re-validated: an image the user deleted
        // cannot be allowed to pin the game to a file that no longer exists.
        [Test]
        public void Session_RememberedImageRemovedFromCandidates_PicksFresh()
        {
            string first = _selector.Select(_gameId, _candidates, null, SelectionMode.Session);

            var reduced = new List<string>(_candidates);
            reduced.Remove(first);

            string replacement = _selector.Select(_gameId, reduced, null, SelectionMode.Session);

            Assert.AreNotEqual(first, replacement);
            Assert.Contains(replacement, reduced);
        }

        [Test]
        public void Session_ClearingCacheAllowsANewChoice()
        {
            string first = _selector.Select(_gameId, _candidates, null, SelectionMode.Session);
            _cache.Clear();

            // Not asserting it differs - a fresh pick may legitimately land on
            // the same file. Asserting the cache was genuinely repopulated.
            _selector.Select(_gameId, _candidates, null, SelectionMode.Session);
            Assert.AreEqual(1, _cache.Count);
        }

        [Test]
        public void Fixed_AlwaysReturnsFirstCandidate()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual("a.jpg", _selector.Select(_gameId, _candidates, null, SelectionMode.Fixed));
            }
        }

        [Test]
        public void Fixed_DoesNotPopulateTheSessionCache()
        {
            _selector.Select(_gameId, _candidates, null, SelectionMode.Fixed);
            Assert.AreEqual(0, _cache.Count);
        }

        [Test]
        public void EverySelection_AvoidsRepeatingThePreviousPick()
        {
            string previous = "c.jpg";

            for (int i = 0; i < 100; i++)
            {
                string pick = _selector.Select(_gameId, _candidates, previous, SelectionMode.EverySelection);
                Assert.AreNotEqual(previous, pick);
                previous = pick;
            }
        }

        [Test]
        public void EverySelection_DoesNotPopulateTheSessionCache()
        {
            _selector.Select(_gameId, _candidates, null, SelectionMode.EverySelection);
            Assert.AreEqual(0, _cache.Count);
        }

        [Test]
        public void EmptyOrNullCandidates_ReturnNullInEveryMode()
        {
            foreach (SelectionMode mode in new[] { SelectionMode.Session, SelectionMode.EverySelection, SelectionMode.Fixed })
            {
                Assert.IsNull(_selector.Select(_gameId, null, null, mode), mode.ToString());
                Assert.IsNull(_selector.Select(_gameId, new List<string>(), null, mode), mode.ToString());
            }
        }

        [Test]
        public void SingleCandidate_ReturnedInEveryMode()
        {
            var one = new List<string> { "only.jpg" };

            foreach (SelectionMode mode in new[] { SelectionMode.Session, SelectionMode.EverySelection, SelectionMode.Fixed })
            {
                Assert.AreEqual("only.jpg", _selector.Select(Guid.NewGuid(), one, "only.jpg", mode), mode.ToString());
            }
        }
    }
}
