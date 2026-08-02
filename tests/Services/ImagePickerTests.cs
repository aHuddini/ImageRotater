using System;
using System.Collections.Generic;
using NUnit.Framework;
using ImageRotater.Services;

namespace ImageRotater.Tests.Services
{
    [TestFixture]
    public class ImagePickerTests
    {
        [Test]
        public void Pick_NullList_ReturnsNull()
        {
            var picker = new ImagePicker(new Random(1));
            Assert.IsNull(picker.Pick(null, null));
        }

        [Test]
        public void Pick_EmptyList_ReturnsNull()
        {
            var picker = new ImagePicker(new Random(1));
            Assert.IsNull(picker.Pick(new List<string>(), null));
        }

        [Test]
        public void Pick_SingleImage_ReturnsIt()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg" };
            Assert.AreEqual("a.jpg", picker.Pick(list, null));
        }

        // A one-image game must keep showing its only image on revisit rather
        // than returning null because "it was the previous pick".
        [Test]
        public void Pick_SingleImage_ReturnsItEvenWhenItWasPreviousPick()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg" };
            Assert.AreEqual("a.jpg", picker.Pick(list, "a.jpg"));
        }

        [Test]
        public void Pick_TwoImages_AlwaysReturnsTheOtherOne()
        {
            var picker = new ImagePicker(new Random(1));
            var list = new List<string> { "a.jpg", "b.jpg" };

            // With two candidates, avoiding the previous pick is deterministic.
            Assert.AreEqual("b.jpg", picker.Pick(list, "a.jpg"));
            Assert.AreEqual("a.jpg", picker.Pick(list, "b.jpg"));
        }

        [Test]
        public void Pick_NeverReturnsPreviousPick_WhenAlternativesExist()
        {
            var picker = new ImagePicker(new Random(12345));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg", "d.jpg" };

            string previous = "c.jpg";
            for (int i = 0; i < 200; i++)
            {
                string pick = picker.Pick(list, previous);
                Assert.AreNotEqual(previous, pick, "picker returned the previous pick");
                previous = pick;
            }
        }

        [Test]
        public void Pick_ReturnsOnlyCandidatesFromTheList()
        {
            var picker = new ImagePicker(new Random(7));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg" };

            for (int i = 0; i < 100; i++)
            {
                Assert.Contains(picker.Pick(list, null), list);
            }
        }

        // Previous pick may name a file that has since been removed from the
        // list. That must not prevent a normal pick.
        [Test]
        public void Pick_PreviousNotInList_StillPicks()
        {
            var picker = new ImagePicker(new Random(3));
            var list = new List<string> { "a.jpg", "b.jpg" };
            Assert.Contains(picker.Pick(list, "gone.jpg"), list);
        }

        [Test]
        public void Pick_EventuallyReturnsEveryCandidate()
        {
            var picker = new ImagePicker(new Random(99));
            var list = new List<string> { "a.jpg", "b.jpg", "c.jpg" };
            var seen = new HashSet<string>();

            string previous = null;
            for (int i = 0; i < 300; i++)
            {
                previous = picker.Pick(list, previous);
                seen.Add(previous);
            }

            Assert.AreEqual(3, seen.Count, "some candidate was never picked");
        }

        // When all candidates are duplicates (all equal each other), Pick must
        // still return promptly and return a member of the list. A retry-loop
        // implementation would hang forever trying to find an alternative.
        [Test]
        public void Pick_AllDuplicates_ReturnsMemberOfListAndTerminates()
        {
            var picker = new ImagePicker(new Random(2));
            var list = new List<string> { "a.jpg", "a.jpg" };
            string result = picker.Pick(list, "a.jpg");
            Assert.Contains(result, list);
            Assert.AreEqual("a.jpg", result);
        }

        // When candidates contain some duplicates, Pick must still return a
        // member of the list and avoid the previous pick if an alternative exists.
        [Test]
        public void Pick_PartialDuplicates_ReturnsMemberOfListAndAvoidsPrevious()
        {
            var picker = new ImagePicker(new Random(4));
            var list = new List<string> { "a.jpg", "a.jpg", "b.jpg" };
            string result = picker.Pick(list, "a.jpg");
            Assert.Contains(result, list);
            // With duplicates of "a.jpg", the allowed list should be ["b.jpg"],
            // so the result should be "b.jpg".
            Assert.AreEqual("b.jpg", result);
        }
    }
}
