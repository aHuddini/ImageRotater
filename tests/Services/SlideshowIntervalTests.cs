using System;
using NUnit.Framework;

namespace ImageRotater.Tests.Services
{
    // How long the slideshow timer should sleep before its next tick.
    //
    // The timer used to tick once a second and ask whether anything was due, so
    // a 10-second interval actually fired somewhere in [10.0, 11.0) - always
    // late, never early, and visibly so at short intervals. Sleeping until the
    // nearest due time instead removes that bias.
    //
    // Two edges make it more than a subtraction. A due time already in the past
    // must not ask the dispatcher for a zero or negative interval, and while
    // the window is unfocused every due time IS in the past - so the floor that
    // keeps a real interval honest would spin the dispatcher for as long as the
    // user was away.
    //
    // The plugin class needs a live Playnite API, so the arithmetic is
    // reproduced here exactly as ArmTimerForNextDue performs it.
    [TestFixture]
    public class SlideshowIntervalTests
    {
        private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan UnfocusedPoll = TimeSpan.FromSeconds(1);

        private static TimeSpan Arm(
            DateTime now, DateTime backgroundDue, DateTime coverDue, bool unfocused)
        {
            if (unfocused)
            {
                return UnfocusedPoll;
            }

            DateTime next = backgroundDue < coverDue ? backgroundDue : coverDue;

            if (next == DateTime.MaxValue)
            {
                return TimeSpan.Zero;   // caller leaves the interval untouched
            }

            TimeSpan wait = next - now;
            return wait > Floor ? wait : Floor;
        }

        [Test]
        public void SleepsExactlyUntilTheDueTime()
        {
            DateTime now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

            TimeSpan wait = Arm(now, now.AddSeconds(10), DateTime.MaxValue, false);

            Assert.AreEqual(TimeSpan.FromSeconds(10), wait,
                "a 10-second interval must wait 10 seconds, not up to 11");
        }

        // Two kinds run on independent clocks; the timer has to serve whichever
        // comes first or the earlier one fires late.
        [Test]
        public void SleepsUntilTheNEARESTOfTheTwoDueTimes()
        {
            DateTime now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(TimeSpan.FromSeconds(6),
                Arm(now, now.AddSeconds(10), now.AddSeconds(6), false));

            Assert.AreEqual(TimeSpan.FromSeconds(4),
                Arm(now, now.AddSeconds(4), now.AddSeconds(9), false));
        }

        // A due time in the past is normal after focus returns - ticks were
        // skipped while away. It must rotate promptly without asking the
        // dispatcher for a non-positive interval.
        [Test]
        public void APastDueTimeFallsBackToTheFloor()
        {
            DateTime now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(Floor, Arm(now, now.AddSeconds(-30), DateTime.MaxValue, false));
            Assert.AreEqual(Floor, Arm(now, now, DateTime.MaxValue, false),
                "exactly due must not produce a zero interval either");
        }

        // The floor is what makes the unfocused case dangerous: every due time
        // is in the past, so without a separate poll the timer would wake every
        // 50ms for as long as the user was away.
        [Test]
        public void UnfocusedPollsSlowlyInsteadOfSpinningOnThePastDueTimes()
        {
            DateTime now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

            TimeSpan wait = Arm(now, now.AddSeconds(-120), now.AddSeconds(-90), true);

            Assert.AreEqual(UnfocusedPoll, wait,
                "chasing past due times while away would spin the dispatcher");

            Assert.Greater(wait, Floor);
        }

        // Both kinds off: nothing to wait for, and the caller leaves the
        // timer's interval alone rather than inventing one.
        [Test]
        public void NothingDueLeavesTheIntervalUntouched()
        {
            DateTime now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(TimeSpan.Zero,
                Arm(now, DateTime.MaxValue, DateTime.MaxValue, false));
        }
    }
}
