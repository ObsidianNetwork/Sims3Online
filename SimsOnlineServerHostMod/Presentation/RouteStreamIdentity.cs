namespace SimsOnlineServerHost
{
    // Fields include the stream sequence at index zero. Rs[6] is a destination
    // tag, not the native route handle shared by Rs[16] and Rx[6].
    internal static class RouteStreamIdentity
    {
        internal static string StartHandle(string[] fields)
        {
            return fields.Length > 16 && fields[1] == "Rs" ? fields[16] : string.Empty;
        }

        internal static bool EndMatches(string[] start, string[] end)
        {
            string handle = StartHandle(start);
            if (handle.Length > 0)
            {
                return end.Length > 6 && end[1] == "Rx" && end[6] == handle;
            }
            return start.Length > 1 && end.Length > 1 && start[1] == "Rt" && end[1] == "Rte";
        }
    }
}
