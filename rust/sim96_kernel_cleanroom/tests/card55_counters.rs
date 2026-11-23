#![forbid(unsafe_code)]

use sim96_kernel_cleanroom::{runtime_bridge::AuthorityWorld, wan_transport::RoleTag, wire::PlayerId};

fn diag(world: &mut AuthorityWorld) -> String {
    let hash = world.state_hash();
    let mut output = Vec::new();
    world.handle_line(PlayerId(1), RoleTag::Server, "DIAG_STATE|99", &mut output);
    assert_eq!(world.state_hash(), hash, "telemetry query changed authority");
    output.remove(0)
}

#[test]
fn card55_e2_grader_requires_measured_native_refusals() {
    let output = std::process::Command::new("python")
        .current_dir(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../scripts"))
        .args(["-c", "from scenarios_test import RoutingEvidence; c=RoutingEvidence(); c.setUp(); c.assertEqual(c.score('E2', dict(c.e2, native_oracle_owned='unmeasured')), 'BLOCKED'); c.assertEqual(c.score('E2', dict(c.e2, native_oracle_owned=None)), 'BLOCKED'); c.assertEqual(c.score('E2', dict(c.e2, native_oracle_owned=0)), 'PASS'); c.assertEqual(c.score('E2', dict(c.e2, native_oracle_owned=1)), 'FAIL')"])
        .output().unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
}

#[test]
fn card55_diag_appends_routing_counters_without_changing_state() {
    let mut world = AuthorityWorld::new();
    assert!(diag(&mut world).ends_with("|navigate_requests=0|plans_served=0|native_oracle_owned=unmeasured"));
    world.attach_routing_census();
    assert!(diag(&mut world).ends_with("|navigate_requests=0|plans_served=0|native_oracle_owned=0"));
    let mut output = Vec::new();
    world.handle_line(PlayerId(1), RoleTag::Client, "CMD|1|ClaimSim|1|100", &mut output);
    for line in [
        "CMD|2|NavigateAStar|1|1000|1024|0",
        "CMD|2|NavigateAStar|1|1000|1024|0",
        "CMD|3|NavigateAStar|1|1000|1101|0",
        "CMD|4|NavigateAStar|bad",
        "CMD|5|NavigateAStar|1|1000|1024|2147483648",
        "CMD|6|NavigateAStar|2|1000|1024|0",
    ] {
        world.handle_line(PlayerId(1), RoleTag::Client, line, &mut output);
    }
    assert!(diag(&mut world).ends_with("|navigate_requests=1|plans_served=1|native_oracle_owned=0"));
}
