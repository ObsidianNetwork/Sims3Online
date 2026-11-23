// Real outgoing SimAnimation batching, only for player 2's motion records.
fn selected_player(record) { record.player_id == "2" }
fn selected_family(record) { record.family == "motion" }
fn split_before(record) { selected_player(record) && selected_family(record) }
