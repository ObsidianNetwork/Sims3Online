using Sims3.SimIFace;

[assembly: Tunable]

namespace SimsOnlineServerHost
{
    public class Initializer
    {
        [Tunable]
        protected static bool kInstantiator = false;

        // Session L's startup method swap (MethodSwap.cs): the game's tuning loader sets this from the package's
        // Current_Tuning before any mod code runs, which is the only switch that reaches the mod before the world loads
        // (a boot sends no bridge command before LOAD_SAVE, and TS3's runtime has no environment reads). make-mod writes
        // it True when SIMSONLINE_METHOD_SWAP=1 is set on the build (Sims3PackageBuilder.InitializerXml).
        [Tunable]
        protected static bool kMethodSwap = false;

        public static bool MethodSwapWanted
        {
            get { return kMethodSwap; }
        }

        private static bool sInitialized;

        static Initializer()
        {
            Initialise();
        }

        public static void Initialise()
        {
            if (sInitialized)
            {
                return;
            }

            ServerHostMain.Initialise();
            sInitialized = true;
        }
    }
}
