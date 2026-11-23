// C#2 ONLY: source-linked into DedicatedServer.Tests; no engine types.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The acquire seam chooses the owner before any stream setup or state is applied.
    internal static class ReplayDriverBootstrap
    {
        internal delegate uint Acquire();

        internal static uint Resolve(uint own, bool bootstrapIdle, Acquire bootstrap, Acquire detached, out bool borrowed)
        {
            borrowed = own != 0u || bootstrapIdle;
            if (own != 0u)
            {
                return own;
            }

            // A failed bootstrap remains pending. A detached fallback would race the later own-machine repair.
            return bootstrapIdle ? bootstrap() : detached();
        }

        internal static bool Pending(ulong host, ulong machine, Dictionary<uint, List<string>> pending, Converter<ulong, ulong> mapHost)
        {
            foreach (List<string> lines in pending.Values)
            {
                string[] acquire = lines[0].Split('|'); // pending driver histories always start with their A
                if (ulong.Parse(acquire[6], CultureInfo.InvariantCulture) == machine
                    && mapHost(ulong.Parse(acquire[3], CultureInfo.InvariantCulture)) == host)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
