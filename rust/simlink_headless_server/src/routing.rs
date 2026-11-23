#![forbid(unsafe_code)]
//! Destination classes at the session boundary; admin sockets are never subscribers.

use crate::{ConnId, Server};
use crate::messages::Event;

#[derive(Clone, Debug)]
pub struct RoutedEvent {
    pub event: Event,
    pub owner: Option<i32>,
    pub requester: Option<ConnId>,
}
/// ClientListenerServer.ClientConnection.IsBroadcastEvent, exactly its eight rows.
pub fn is_broadcast_event(kind: &str) -> bool {
    matches!(kind, "SimClaimed" | "SimReleased" | "SimAutonomyChanged" | "SessionJoined"
        | "GameSpeedChanged" | "WorldTimeChanged" | "SaveCheckpointed" | "LotReconcileAcknowledged")
}
pub fn recipients(server: &Server, routed: &RoutedEvent) -> Vec<ConnId> {
    let kind = routed.event.kind.as_str();
    let requester = routed.requester.and_then(|id| server.event_connection(id));
    if matches!(kind, "SnapshotDelta" | "FullSnapshot" | "SimAnimation" | "LotLocalAuthorityModeChanged") && routed.requester.is_some() {
        return requester.into_iter().collect();
    }
    if matches!(kind, "SimDescriptionData" | "WorldFile" | "WorldFileChunk" | "PickFailed") {
        return requester.into_iter().collect();
    }
    if matches!(kind, "DialogRequested" | "Sting" | "LotLocalAuthorityModeChanged" | "LotExportRequested") {
        return routed.owner.and_then(|player| server.active_connection(player))
            .filter(|id| !server.connection(*id).is_some_and(|c| c.admin)).into_iter().collect();
    }
    if is_broadcast_event(kind) || matches!(kind,
        "SnapshotDelta" | "SimAnimation" | "InteractionQueued" | "InteractionReplayQueued" | "InteractionStarted"
        | "InteractionCompleted" | "InteractionFailed" | "SimMotiveChanged" | "SimSkillChanged"
        | "HouseholdFundsChanged" | "SimStateChanged" | "SimSpawned" | "SimDespawned" | "SimDetailsChanged")
    {
        let mut recipients = server.joined_connections();
        if matches!(kind, "SimReleased" | "SimAutonomyChanged") {
            if let Some(id) = requester.filter(|id| server.connection(*id).is_some_and(|connection| !connection.admin && !connection.is_superseded())) {
                if !recipients.contains(&id) { recipients.push(id); recipients.sort(); }
            }
        }
        return recipients;
    }
    requester.into_iter().collect()
}
