# Recorded refusal fixture

`recorded_error.txt` is a non-proprietary error response used to test refusal
decoding. It contains no game files, saves, or personal data. Other decoder
cases are synthetic test inputs and do not represent captured game sessions.

## Card 60 reply corpus

`v1_reply_corpus/` contains the five v1 request/reply contracts observed in
the offline Rust socket run on November 9, 2026. They port the pinned
`SidecarCommandReplyTests` cases checked by `tests/reply_contracts.rs`;
they are not live TS3W recordings or a new C# differential.

Each file starts with a fresh, welcomed public connection. Rows contain
`request TAB reply`. DuplicateJoin and UnknownTypeCommand include their
successful join first, preserving the recorded session's authority state.
The replay validates the OS-random resume credential before removing only
its tagged suffix for comparison and recording, so no credential is stored.
All refusal bytes and correlations must match exactly. The existing five-case
test and C# source count pin remain separate and unchanged.

With `CARD57_ARTIFACT_DIR` set, replay writes
`v1_reply_corpus_replay.txt` beside `reply_contracts.txt`. Passing this
offline corpus completes only the Rust replay portion of Card 60 A1;
Cards 55 and 60 retain their live acceptance gates.
