using System;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Weather;

namespace SimsOnline.Probes
{
    public class EnvironmentAuthorityProbe
    {
        private static bool LocalStagingHeld()
        {
            object before = WeatherControl.Settings;
            FieldInfo[] fields = typeof(WeatherSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            object attempted = Activator.CreateInstance(typeof(WeatherSettings));
            for (int i = 0; i < fields.Length; i++)
                fields[i].SetValue(attempted, fields[i].GetValue(before));
            typeof(WeatherSettings).GetField("CloudinessTarget").SetValue(attempted,
                ((WeatherSettings)before).CloudinessTarget + 0.125f);
            bool same = true;
            try
            {
                WeatherControl.Settings = (WeatherSettings)attempted;
                PropertyInfo[] properties = typeof(WeatherControl).GetProperties(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo property = properties[i];
                    if (!property.CanRead || !property.CanWrite) continue;
                    object value = property.GetValue(null, null);
                    if (property.PropertyType == typeof(float)) value = (float)value + 0.125f;
                    else if (property.PropertyType == typeof(int)) value = (int)value + 1;
                    else if (property.PropertyType == typeof(bool)) value = !(bool)value;
                    else continue;
                    property.SetValue(null, value, null);
                }
                WeatherControl.SetTreeLeafCover(0.25f, 0.75f, true);
                WeatherControl.SetSunriseAndSunsetTimeOffsets(0.5f, 0.25f);
                WeatherControl.SendSettings();
                object after = WeatherControl.Settings;
                for (int i = 0; i < fields.Length; i++)
                    if (!object.Equals(fields[i].GetValue(before), fields[i].GetValue(after))) same = false;
            }
            finally
            {
                WeatherControl.Settings = (WeatherSettings)before;
            }
            return same;
        }

        public static string Run()
        {
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            Type host = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (host == null) host = assembly.GetType("SimsOnlineServerHost.ServerHostMain");
            if (host == null) return "ok=false;error=no host";
            bool client = (bool)host.GetProperty("SkipLocalEnvironment", flags).GetValue(null, null);
            bool stagingHeld = !client || LocalStagingHeld();
            int timers = 0;
            string[] fields = new string[] { "mLunarCycleAlarm", "mLunarEffectsAlarm", "mSunRiseAlarm", "mZombieAlarm" };
            for (int i = 0; i < fields.Length; i++)
            {
                AlarmHandle alarm = (AlarmHandle)typeof(LunarCycleManager).GetField(fields[i], flags).GetValue(null);
                if (alarm != AlarmHandle.kInvalidHandle) timers++;
            }
            WeatherSettings weather = WeatherControl.Settings;
            bool seasonMatches = !SeasonsManager.Enabled || weather.CurrentSeason == SeasonsManager.CurrentSeason;
            CultureInfo c = CultureInfo.InvariantCulture;
            return "ok=" + (stagingHeld && (!client || (timers == 0 && seasonMatches)) ? "true" : "false")
                + ";client=" + client + ";stagingHeld=" + stagingHeld + ";lunarTimers=" + timers
                + ";phase=" + World.GetLunarPhase().ToString(c) + ";cycle=" + World.GetLunarCycle().ToString(c)
                + ";season=" + weather.CurrentSeason.ToString()
                + ";modelSeason=" + (SeasonsManager.Enabled ? SeasonsManager.CurrentSeason.ToString() : "off")
                + ";rain=" + weather.PrecipitationStrength.ToString("R", c)
                + ";wind=" + weather.WindStrength.ToString("R", c)
                + ";cloud=" + weather.CloudinessTarget.ToString("R", c);
        }
    }
}
