using System;
using System.Reflection;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;
using Sims3.SimIFace.Weather;

namespace SimsOnlineServerHost
{
    internal static class EnvironmentReplay
    {
        internal static long Applied;
        internal static long Failed;
        internal static string Last = "-";
        private static FieldInfo sZombieAlarm;

        // The native sky setters alone do not stop LunarCycleManager from creating its
        // own zombie/lunacy effects. Hold the whole client service, not individual socials.
        internal static void HoldLocalLunarService()
        {
            LunarCycleManager.EndLunarCycle();
            if (sZombieAlarm == null)
            {
                sZombieAlarm = typeof(LunarCycleManager).GetField("mZombieAlarm", BindingFlags.NonPublic | BindingFlags.Static);
            }
            AlarmHandle alarm = (AlarmHandle)sZombieAlarm.GetValue(null);
            if (alarm != AlarmHandle.kInvalidHandle)
            {
                AlarmManager.Global.RemoveAlarm(alarm);
                sZombieAlarm.SetValue(null, AlarmHandle.kInvalidHandle);
            }
        }

        internal static bool Replay(string[] fields)
        {
            string operation;
            float[] values;
            if (!EnvironmentState.TryParse(fields, out operation, out values))
            {
                Failed++;
                return false;
            }

            try
            {
                if (operation == "weather")
                {
                    WeatherSettings settings = Settings(values);
                    WeatherControl.Settings = settings;
                    WeatherControl.SendSettings();
                    MirrorSeason(settings.CurrentSeason);
                }
                else if (operation == "SetLunarCycle")
                {
                    World.SetLunarCycle((int)values[0]);
                }
                else if (operation == "SetLunarPhase")
                {
                    World.SetLunarPhase((int)values[0]);
                }
                else if (operation == "ForceLunarCycle")
                {
                    World.ForceLunarCycle((int)values[0]);
                }
                else if (operation == "ForceLunarPhase")
                {
                    World.ForceLunarPhase((int)values[0]);
                }
                else if (operation == "SetLightingValues")
                {
                    World.SetLightingValues((LightTuning)(int)values[0]);
                }
                else if (operation == "RandomizeCloudinessImmediately")
                {
                    World.RandomizeCloudinessImmediately(values[0]);
                }

                Applied++;
                Last = operation;
                return true;
            }
            catch (Exception)
            {
                Failed++;
                return false;
            }
        }

        internal static WeatherSettings Settings(float[] v)
        {
            WeatherSettings s = new WeatherSettings();
            s.PrecipitationType = (PrecipitationType)(int)v[0];
            s.PrecipitationStrength = v[1];
            s.CurrentSeason = (Season)(int)v[2];
            s.SunriseOffsetTime = v[3];
            s.SunsetOffsetTime = v[4];
            s.PrecipitationIntensityAudioIndex = (int)v[5];
            s.WindIntensityAudioIndex = (int)v[6];
            s.WeatherTerrain = (WeatherTerrainType)(int)v[7];
            s.FrostAmount = v[8];
            s.SnowThicknessAmount = v[9];
            s.SnowRoadTrackDepthAmount = v[10];
            s.WetAmount = v[11];
            s.TreeLeafLossAmount = v[12];
            s.UseFallSetForTreeLeafLoss = v[13] != 0f;
            s.TreeFallColorAmount = v[14];
            s.PrecipitationFogStrength = v[15];
            s.PondFreezeAmount = v[16];
            s.HeatShimmerDistance = v[17];
            s.WorldLeavesAmount = v[18];
            s.IsSnowMelting = v[19] != 0f;
            s.CloudinessTarget = v[20];
            s.WindStrength = v[21];
            s.NormalFogStrength = v[22];
            return s;
        }

        // Menu tests read SeasonsManager.CurrentSeason, not WeatherControl.CurrentSeason. Change only its model
        // field: ForceRequestSeason would run gameplay, transitions and weather generation on the presenter.
        private static void MirrorSeason(Season season)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo singleton = typeof(SeasonsManager).GetField("sInstance", BindingFlags.NonPublic | BindingFlags.Static);
            object manager = singleton.GetValue(null);
            if (manager == null)
            {
                return; // Seasons is not enabled in this world.
            }

            object progress = typeof(SeasonsManager).GetField("mProgressManager", flags).GetValue(manager);
            progress.GetType().GetField("mCurrentSeason", flags).SetValue(progress, season);
        }

        internal static void Reset()
        {
            Applied = 0;
            Failed = 0;
            Last = "-";
        }
    }
}
