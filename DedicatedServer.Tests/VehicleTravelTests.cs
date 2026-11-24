using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("VehicleTravel.RouteEndUsesHandleNotDestinationTag")]
    private static void VehicleTravelRouteEndUsesHandleNotDestinationTag()
    {
        string[] start = "1|Rs|10|1|2|3|7|0|0||100|0|0|0|0|0|99".Split('|');
        Assert(RouteStreamIdentity.StartHandle(start) == "99",
            "Route starts carry their handle separately from destination tags.");
        Assert(RouteStreamIdentity.EndMatches(start, "2|Rx|10|1|2|3|99|200".Split('|')),
            "The matching native end must finish the pending foot leg.");
        Assert(!RouteStreamIdentity.EndMatches(start, "2|Rx|10|1|2|3|7|200".Split('|')),
            "A destination tag cannot stand in for a different route's handle.");
        Assert(RouteStreamIdentity.EndMatches("1|Rt|10|1|2|3|7".Split('|'), "2|Rte|10|1|2|3".Split('|')),
            "Legacy polling routes retain their actor-scoped terminal signal.");
    }

    private static bool VehicleLine(VehicleTravel travel, string line)
    {
        return travel.Apply(("1|" + line).Split('|'));
    }

    [Test("VehicleTravel.RouteEndDoesNotDismountPassengers")]
    private static void VehicleTravelRouteEndDoesNotDismountPassengers()
    {
        var travel = new VehicleTravel();
        Assert(VehicleLine(travel, "Vh|10|0|0|enter|1") && travel.Holds(10),
            "The get-in route action holds the position mirror before the vehicle is known.");
        Assert(VehicleLine(travel, "Vh|10|100|5|ride|2"), "The actual posture binds the driver seat.");
        Assert(VehicleLine(travel, "Vh|11|100|6|ride|2"), "Passengers share the same vehicle pose.");
        Assert(VehicleLine(travel, "Vs|100|1000|5|0|8|-|22"), "The vehicle's own route opens.");
        Assert(VehicleLine(travel, "Vp|100|3|4|0|7|0|1"), "The last moving frame is captured.");
        Assert(VehicleLine(travel, "Ve|100|3|5|0|8|22") && !travel.HasRoute(100),
            "The matching route ends.");
        var end = travel.Poses.Single().Value;
        Assert(end.X == 5 && end.Z == 8 && end.ForwardZ == 1,
            "Arrival supplies the final vehicle position even in the last moving frame's tick.");
        Assert(travel.Holds(10) && travel.Holds(11), "A route end never releases an occupied vehicle.");
        Assert(VehicleLine(travel, "Vh|10|100|5|release|4") && !travel.Holds(10) && travel.Holds(11),
            "Only the server's dismount releases that passenger.");
    }

    [Test("VehicleTravel.OldRideCannotReleaseNewVehicle")]
    private static void VehicleTravelOldRideCannotReleaseNewVehicle()
    {
        var travel = new VehicleTravel();
        Assert(VehicleLine(travel, "Vh|10|100|5|ride|2"), "First ride binds.");
        Assert(VehicleLine(travel, "Vh|10|200|7|ride|4"), "A new authoritative vehicle replaces the old one.");
        Assert(!VehicleLine(travel, "Vh|10|100|5|release|5") && travel.Holds(10),
            "A delayed release from another vehicle cannot detach the new ride.");
        Assert(!VehicleLine(travel, "Vh|10|200|7|release|3"), "An older observation cannot release the new ride.");
        Assert(VehicleLine(travel, "Vs|200|1000|5|0|8|-|22"), "First route opens.");
        Assert(VehicleLine(travel, "Vs|200|1000|5|0|8|-|23"), "A new leg replaces it.");
        Assert(!VehicleLine(travel, "Ve|200|5|5|0|8|22") && travel.HasRoute(200),
            "The previous leg's end cannot end the current one.");
    }

    [Test("VehicleTravel.JoinPoseAndOutOfOrderFrames")]
    private static void VehicleTravelJoinPoseAndOutOfOrderFrames()
    {
        var travel = new VehicleTravel();
        Assert(VehicleLine(travel, "Vp|100|20|40|1|80|0|1"), "A late-join pose needs no prior route start.");
        Assert(VehicleLine(travel, "Vh|10|100|5|ride|20") && travel.Holds(10),
            "A late join needs no synthetic get-in interaction.");
        Assert(!VehicleLine(travel, "Vp|100|19|0|0|0|1|0"), "Old poses cannot rewind the ride.");
        Assert(!VehicleLine(travel, "Vp|100|20|0|0|0|1|0"), "A duplicate tick is not applied twice.");
        Assert(VehicleLine(travel, "Vp|100|21|41|1|81|0|1"), "The next authoritative frame advances.");
        var pose = travel.Poses.Single().Value;
        Assert(pose.Tick == 21 && pose.X == 41 && pose.Z == 81, "The exact accepted transform survives.");
        travel.Clear();
        Assert(!travel.Holds(10) && !travel.Poses.Any() && !travel.HasRoute(100), "A reset clears all trip state.");
    }

    [Test("VehicleTravel.RejectsMalformedBoundaryData")]
    private static void VehicleTravelRejectsMalformedBoundaryData()
    {
        var travel = new VehicleTravel();
        Assert(!VehicleLine(travel, "Vh|10|0|5|ride|1"), "A seated rider must name a vehicle.");
        Assert(!VehicleLine(travel, "Vh|10|100|4294967296|ride|1"), "A slot must fit its native uint.");
        Assert(!VehicleLine(travel, "Vp|100|1|NaN|0|0|0|1"), "A nonfinite transform is not applied.");
        Assert(!VehicleLine(travel, "Vp|100|1|0|0|0|0|Infinity"), "A nonfinite facing is not applied.");
        Assert(!VehicleLine(travel, "Vp|0|1|0|0|0|0|1"), "The invalid object cannot receive a transform.");
        Assert(!VehicleLine(travel, "Vh|10|100|5|drive|1") && !travel.Holds(10), "Unknown phases acquire no hold.");
    }
}
