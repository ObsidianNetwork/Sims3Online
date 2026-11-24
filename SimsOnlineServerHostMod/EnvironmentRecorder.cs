using System;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;
using Sims3.SimIFace.Weather;

namespace SimsOnlineServerHost
{
    // IWeatherControl is the common boundary for every weather producer. ScriptCore stages its properties
    // until SendSettings; capture the complete committed value there so precipitation type, strength and sound agree.
    internal sealed class EnvironmentRecorder : IWeatherControl
    {
        private readonly IWeatherControl mInner;
        internal static long Lines;

        private EnvironmentRecorder(IWeatherControl inner)
        {
            mInner = inner;
        }

        internal static FieldInfo HolderField()
        {
            return typeof(WeatherControl).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = HolderField();
                return field != null && field.GetValue(null) is EnvironmentRecorder;
            }
        }

        internal static string Install()
        {
            try
            {
                FieldInfo field = HolderField();
                if (field == null)
                {
                    return "environment=unavailable";
                }

                IWeatherControl current = field.GetValue(null) as IWeatherControl;
                if (current == null)
                {
                    return "environment=no-instance";
                }

                if (!(current is EnvironmentRecorder))
                {
                    EnvironmentRecorder proxy = new EnvironmentRecorder(current);
                    field.SetValue(null, proxy);
                    AppDomain.CurrentDomain.SetData("WeatherControl", proxy);
                }

                Capture();
                return "environment=on";
            }
            catch (Exception ex)
            {
                return "environment=error:" + ex.GetType().Name;
            }
        }

        // Called at recorder enable / snapshot too: a late join must not wait for the next weather tick or moon change.
        internal static void Capture()
        {
            try
            {
                FieldInfo field = HolderField();
                IWeatherControl current = field == null ? null : field.GetValue(null) as IWeatherControl;
                if (current != null)
                {
                    Record(EnvironmentState.WeatherLine(Values(current.Settings)), true);
                }

                RecordWorld("SetLunarCycle", World.GetLunarCycle());
                RecordWorld("SetLunarPhase", World.GetLunarPhase());
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void Record(string line, bool canSkip)
        {
            try
            {
                if (!SacsRecorder.RingEnabled)
                {
                    return;
                }

                // BoundaryState's global En keys survive object sweeps and are included for every joiner.
                BoundaryState.Remember(line, 3);
                if (!BoundaryState.Repeat(line, canSkip))
                {
                    Lines++;
                    SacsRecorder.RingLine(line);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        internal static void RecordWorld(string operation, int value)
        {
            Record("En|0|" + operation + "|" + value.ToString(CultureInfo.InvariantCulture), false);
        }

        internal static void RecordWorld(string operation, float value)
        {
            Record("En|0|" + operation + "|" + value.ToString("R", CultureInfo.InvariantCulture), false);
        }

        // An explicit, fixed wire shape; no reflection enumeration order or localized enum text.
        internal static float[] Values(WeatherSettings s)
        {
            return new float[] {
                (float)s.PrecipitationType, s.PrecipitationStrength, (float)s.CurrentSeason,
                s.SunriseOffsetTime, s.SunsetOffsetTime, s.PrecipitationIntensityAudioIndex, s.WindIntensityAudioIndex,
                (float)s.WeatherTerrain, s.FrostAmount, s.SnowThicknessAmount, s.SnowRoadTrackDepthAmount, s.WetAmount,
                s.TreeLeafLossAmount, s.UseFallSetForTreeLeafLoss ? 1f : 0f, s.TreeFallColorAmount,
                s.PrecipitationFogStrength, s.PondFreezeAmount, s.HeatShimmerDistance, s.WorldLeavesAmount,
                s.IsSnowMelting ? 1f : 0f, s.CloudinessTarget, s.WindStrength, s.NormalFogStrength
            };
        }

        // Settings is also the script-side weather model. Blocking only SendSettings
        // would leave client gameplay reading locally staged weather instead of the server's.
        public WeatherSettings Settings { get { return mInner.Settings; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.Settings = value; } }
        public PrecipitationType PrecipitationType { get { return mInner.PrecipitationType; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.PrecipitationType = value; } }
        public float PrecipitationStrength { get { return mInner.PrecipitationStrength; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.PrecipitationStrength = value; } }
        public int PrecipitationIntensityAudioIndex { get { return mInner.PrecipitationIntensityAudioIndex; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.PrecipitationIntensityAudioIndex = value; } }
        public float WindStrength { get { return mInner.WindStrength; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.WindStrength = value; } }
        public int WindIntensityAudioIndex { get { return mInner.WindIntensityAudioIndex; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.WindIntensityAudioIndex = value; } }
        public WeatherTerrainType WeatherTerrain { get { return mInner.WeatherTerrain; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.WeatherTerrain = value; } }
        public float FrostAmount { get { return mInner.FrostAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.FrostAmount = value; } }
        public float SnowThicknessAmount { get { return mInner.SnowThicknessAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.SnowThicknessAmount = value; } }
        public bool IsSnowMelting { get { return mInner.IsSnowMelting; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.IsSnowMelting = value; } }
        public float SnowRoadTrackDepthAmount { get { return mInner.SnowRoadTrackDepthAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.SnowRoadTrackDepthAmount = value; } }
        public float WetAmount { get { return mInner.WetAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.WetAmount = value; } }
        public float PrecipitationFogStrength { get { return mInner.PrecipitationFogStrength; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.PrecipitationFogStrength = value; } }
        public float PondFreezeAmount { get { return mInner.PondFreezeAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.PondFreezeAmount = value; } }
        public float HeatShimmerDistance { get { return mInner.HeatShimmerDistance; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.HeatShimmerDistance = value; } }
        public Season CurrentSeason { get { return mInner.CurrentSeason; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.CurrentSeason = value; } }
        public float WorldLeavesAmount { get { return mInner.WorldLeavesAmount; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.WorldLeavesAmount = value; } }
        public float CloudinessTarget { get { return mInner.CloudinessTarget; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.CloudinessTarget = value; } }
        public float NormalFogStrength { get { return mInner.NormalFogStrength; } set { if (!ServerHostMain.SkipLocalEnvironment) mInner.NormalFogStrength = value; } }

        public void SetTreeLeafCover(float fallColorAmount, float leafLossAmount, bool isLosingLeaves)
        {
            if (ServerHostMain.SkipLocalEnvironment) return;
            mInner.SetTreeLeafCover(fallColorAmount, leafLossAmount, isLosingLeaves);
        }

        public void SetSunriseAndSunsetTimeOffsets(float sunriseOffset, float sunsetOffset)
        {
            if (ServerHostMain.SkipLocalEnvironment) return;
            mInner.SetSunriseAndSunsetTimeOffsets(sunriseOffset, sunsetOffset);
        }

        public void SendSettings()
        {
            if (ServerHostMain.SkipLocalEnvironment)
            {
                return;
            }
            mInner.SendSettings();
            Record(EnvironmentState.WeatherLine(Values(mInner.Settings)), true);
        }
    }
}
