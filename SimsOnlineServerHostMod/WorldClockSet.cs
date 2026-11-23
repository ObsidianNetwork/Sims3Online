// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

namespace SimsOnlineServerHost
{
    // where SET_WORLD_TIME moves the clock. The wire carries a day of the week, hours and minutes, no week, and
    // the engine's SimClock.SetStartTime(day, hours) always lands in week 0 (SimClock.cs:88). A game that loaded a save
    // past week 0 jumped back whole weeks on its first set, and every alarm, the HUD clock's minute alarm included
    // (HudModel.cs:3161), then waited those weeks: alarms keep absolute due times (AlarmManager.Simulate). So the set
    // moves the clock by the shortest way to the server's minute, within half a week either side, and not at all when
    // the game already shows that minute: the wire truncates to the minute, so a game a few seconds ahead would
    // otherwise step back on every reconcile.
    internal static class WorldClockSet
    {
        // Engine units: SimClock's 37.5 ticks a game minute.
        internal const long TicksPerTwoMinutes = 75;
        internal const int MinutesPerDay = 1440;
        internal const int MinutesPerWeek = 7 * MinutesPerDay;

        // The ticks to add to the clock so it reads day (0 = Sunday), hours and minutes: 0 when the game already reads
        // that minute, else a move to the start of that minute, never more than half a week either way.
        internal static long DeltaTicks(long currentTicks, int day, int hours, int minutes)
        {
            long currentMinute = (currentTicks * 2) / TicksPerTwoMinutes;
            int currentOfWeek = (int)(currentMinute % MinutesPerWeek);
            int targetOfWeek = (day * MinutesPerDay) + (hours * 60) + minutes;
            int move = (targetOfWeek - currentOfWeek) % MinutesPerWeek;
            if (move < 0)
            {
                move += MinutesPerWeek;
            }

            if (move > MinutesPerWeek / 2)
            {
                move -= MinutesPerWeek;
            }

            if (move == 0)
            {
                return 0;
            }

            return MinuteStartTicks(currentMinute + move) - currentTicks;
        }

        // The first tick that reads as that minute (odd minutes start half way into a tick).
        internal static long MinuteStartTicks(long minute)
        {
            return ((minute * TicksPerTwoMinutes) + 1) / 2;
        }
    }
}
