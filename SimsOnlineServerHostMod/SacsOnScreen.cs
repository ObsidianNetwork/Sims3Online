using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The on-screen walk (SimLink/src/onscreen.cpp, native version 12). SimLink rewrites the entry of the route
    // follower's on-screen test at load so that it answers true: every Sim walks the animated, on-screen way in every
    // game, whatever the game's cameras look at (onscreen_logic.h). On in every game unless SIMLINK_ONSCREEN=0. The mod
    // only reports it.
    //
    // Status field (SACS_RECORDER status): onScreen=on|off,installed=N,site=N,sites=N. installed is SimLink's install
    // state (1 installed; 0 not attempted: SIMLINK_ONSCREEN=0; 2 kill switch, 3 another build, 4 a byte mismatch at row
    // `site`, 5 a page that could not be made writable at row `site`, 6 exception); on means installed=1. sites is the
    // rows written: 1 when on, 0 otherwise. Before native version 12: onScreen=off,ready=0.
    internal static class SacsOnScreen
    {
        internal static string Describe()
        {
            if (!SimLinkShadow.OnScreenReady)
            {
                return "onScreen=off,ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.OnScreen(0);
                int installed = state & 0xFF;
                return "onScreen=" + (installed == 1 ? "on" : "off") + ",installed=" + installed.ToString(c)
                    + ",site=" + (state >> 8).ToString(c) + ",sites=" + SimLinkShadow.OnScreen(1).ToString(c);
            }
            catch (Exception ex)
            {
                return "onScreen=error:" + ex.GetType().Name;
            }
        }
    }
}
