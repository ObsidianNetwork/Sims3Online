"""Recorded Card 55 page bodies; no sockets, game calls or captured geometry."""
import unittest
from unittest.mock import Mock

from scripts import routing_collectors as collectors
from scripts import routing_evidence


ACTOR = 18031287978496800304
BRIDGE_CORR = "0123456789abcdef0123456789abcdef"
# Section 3.8, worked example 5. These are authored contract bytes, not a live capture.
PAGE_ZERO = (
    b"rust-route-plans=page;epoch=2;corr=1;actor=18031287978496800304;"
    b"cursor=0;count=2;total=3;next=2;more=1\n"
    b"Rt|1|18031287978496800304|2|0123456789abcdef|2000\n"
    b"Vp|rt|1|18031287978496800304|0|0|500|0|100"
)
PAGE_LAST = (
    b"rust-route-plans=page;epoch=2;corr=1;actor=18031287978496800304;"
    b"cursor=2;count=1;total=3;next=3;more=0\n"
    b"Vp|rt|1|18031287978496800304|1|0|500|0|100"
)
CLIENT_ZERO = (
    b"rust-route-client=page;epoch=2;corr=1;actor=18031287978496800304;"
    b"legs_total=2;received_legs=2;executed_count=2;cursor=0;count=1;next=1;more=1;"
    b"WalkPhase=Halted;invalid_progressions=0;executed_slice=[0]"
)
CLIENT_LAST = (
    b"rust-route-client=page;epoch=2;corr=1;actor=18031287978496800304;"
    b"legs_total=2;received_legs=2;executed_count=2;cursor=1;count=1;next=2;more=0;"
    b"WalkPhase=Halted;invalid_progressions=0;executed_slice=[1]"
)
REQUIRED = bytes.fromhex(
    "727573742D726F7574652D636C69656E743D626C6F636B65643B726561736F6E3D65706F63682D7265717569726564"
)
MISMATCH = bytes.fromhex(
    "727573742D726F7574652D636C69656E743D626C6F636B65643B726561736F6E3D65706F63682D6D69736D61746368"
)
# Rust admin.rs error() at 21812665 wraps the same refusal body in this JSON.
DAEMON_REQUIRED = b'{"ok":false,"error":"rust-route-client=blocked;reason=epoch-required"}'
DAEMON_MISMATCH = b'{"ok":false,"error":"rust-route-client=blocked;reason=epoch-mismatch"}'


class PageParser(unittest.TestCase):
    def test_section_38_header_bytes(self):
        header = PAGE_ZERO.split(b"\n")[0]
        self.assertEqual(header.hex().upper(),
            "727573742D726F7574652D706C616E733D706167653B65706F63683D323B636F72723D313B6163746F723D"
            "31383033313238373937383439363830303330343B637572736F723D303B636F756E743D323B746F74616C"
            "3D333B6E6578743D323B6D6F72653D31")
        page = collectors.parse_page(PAGE_ZERO)
        self.assertEqual((page["epoch"], page["corr"], page["actor"]), (2, 1, ACTOR))
        self.assertEqual(page["frames"], PAGE_ZERO.decode("ascii").split("\n")[1:])
        self.assertEqual(collectors.parse_page(CLIENT_ZERO)["executed_slice"], [0])

    def test_both_headers_require_epoch_as_first_key(self):
        for body in (PAGE_ZERO, CLIENT_ZERO):
            for invalid in (
                body.replace(b";epoch=2", b""),
                body.replace(b";epoch=2;corr=1", b";corr=1;epoch=2"),
                body.replace(b";epoch=2", b";epoch=2;epoch=2"),
            ):
                with self.subTest(body=body[:21], invalid=invalid[:70]):
                    with self.assertRaises(routing_evidence.Missing):
                        collectors.parse_page(invalid)

    def test_canonical_positive_u64_epoch(self):
        for body in (PAGE_ZERO, CLIENT_ZERO):
            for epoch in (b"0", b"02", b"+2", b"-2", b" 2", b"2 ", b"18446744073709551616", b""):
                with self.subTest(epoch=epoch, kind=body[:21]):
                    with self.assertRaises(routing_evidence.Missing):
                        collectors.parse_page(body.replace(b"epoch=2", b"epoch=" + epoch))
            self.assertEqual(collectors.parse_page(
                body.replace(b"epoch=2", b"epoch=18446744073709551615"))["epoch"], (1 << 64) - 1)

    def test_both_epoch_refusals_remain_refusals(self):
        for body, reason in ((REQUIRED, "epoch-required"), (MISMATCH, "epoch-mismatch")):
            self.assertEqual(len(body), 47)
            with self.assertRaises(collectors.PageRefused) as caught:
                collectors.parse_page(body)
            self.assertEqual(caught.exception.reason, reason)

    def test_json_wrapped_daemon_refusals_preserve_reason(self):
        for body, reason in ((DAEMON_REQUIRED, "epoch-required"), (DAEMON_MISMATCH, "epoch-mismatch")):
            with self.subTest(reason=reason):
                with self.assertRaises(collectors.PageRefused) as caught:
                    collectors.parse_page(body)
                self.assertEqual(caught.exception.reason, reason)

    def test_malformed_daemon_error_envelopes_are_unavailable(self):
        for body in (
            b'{"ok":false,"error":',
            b'{"ok":false,"error":null}',
            b'{"ok":true,"error":"rust-route-client=blocked;reason=epoch-mismatch"}',
        ):
            with self.subTest(body=body):
                with self.assertRaises(routing_evidence.Missing):
                    collectors.parse_page(body)

    def test_page_framing_is_validated_before_join(self):
        for bad in (
            PAGE_ZERO.replace(b"count=2", b"count=1"),
            PAGE_ZERO.replace(b"next=2", b"next=0"),
            PAGE_ZERO.replace(b"more=1", b"more=0"),
            PAGE_ZERO.replace(b"total=3", b"total=1"),
            CLIENT_ZERO.replace(b"count=1", b"count=2"),
            CLIENT_ZERO.replace(b"executed_slice=[0]", b"executed_slice=[-1]"),
            CLIENT_ZERO + b"\nextra",
            PAGE_ZERO + b"\xff",
        ):
            with self.subTest(bad=bad[:100]):
                with self.assertRaises(routing_evidence.Missing):
                    collectors.parse_page(bad)


class PageRequests(unittest.TestCase):
    def test_daemon_request_bytes(self):
        self.assertEqual(collectors.daemon_plan_request(1, 0, 2), "RUST_ROUTE_PLANS 1 0 2")
        self.assertEqual(collectors.daemon_plan_request(1, 2, 16, 2), "RUST_ROUTE_PLANS 1 2 16 2")

    def test_client_continuation_bytes_for_both_kinds(self):
        expected = bytes.fromhex(
            "534143535F5245434F524445527C30313233343536373839616263646566303132333435363738396162636465667C"
            "727573742D726F7574652D706C616E737C317C31383033313238373937383439363830303330347C327C31367C32"
        )
        command = collectors.client_page_request(BRIDGE_CORR, "rust-route-plans", 1, ACTOR, 2, 16, 2)
        self.assertEqual(command.encode("ascii"), expected)
        self.assertEqual(len(expected), 93)
        self.assertEqual(collectors.client_page_request(
            BRIDGE_CORR, "rust-route-client", 1, ACTOR, 2, 16, 2),
            command.replace("rust-route-plans", "rust-route-client"))

    def test_continuation_without_epoch_is_rejected_locally(self):
        for build in (
            lambda: collectors.daemon_plan_request(1, 2),
            lambda: collectors.client_page_request(BRIDGE_CORR, "rust-route-plans", 1, ACTOR, 2),
            lambda: collectors.client_page_request(BRIDGE_CORR, "rust-route-client", 1, ACTOR, 2),
        ):
            with self.assertRaises(collectors.PageRefused) as caught:
                build()
            self.assertEqual(caught.exception.reason, "epoch-required")


class Collection(unittest.TestCase):
    def test_json_wrapped_refusals_restart_and_discard_old_frames(self):
        for refusal in (DAEMON_REQUIRED, DAEMON_MISMATCH):
            with self.subTest(refusal=refusal):
                request = Mock(side_effect=[
                    PAGE_ZERO, refusal,
                    PAGE_ZERO.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                    PAGE_LAST.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                ])
                result = collectors.collect_pages(request, "rust-route-plans", 1, ACTOR)
                self.assertEqual(request.call_args_list, [
                    ((0, None),), ((2, 2),), ((0, None),), ((2, 3),)])
                self.assertEqual(result["epoch"], 3)
                self.assertEqual(len(result["frames"]), 3)
                self.assertNotIn("|500|", "\n".join(result["frames"]))

    def test_client_epoch_newer_than_daemon_observation_is_accepted(self):
        request = Mock(side_effect=[CLIENT_ZERO, CLIENT_LAST])
        result = collectors.collect_pages(request, "rust-route-client", 1, ACTOR, daemon_epoch=1)
        self.assertEqual(result["epoch"], 2)
        self.assertEqual(result["executed_slice"], [0, 1])
        self.assertEqual(request.call_args_list, [((0, None),), ((1, 2),)])

    def test_newer_epoch_during_paging_restarts_with_stale_daemon_observation(self):
        for kind, first, last in (
            ("rust-route-plans", PAGE_ZERO, PAGE_LAST),
            ("rust-route-client", CLIENT_ZERO, CLIENT_LAST),
        ):
            with self.subTest(kind=kind):
                request = Mock(side_effect=[
                    first, last.replace(b"epoch=2", b"epoch=3"),
                    first.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                    last.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                ])
                result = collectors.collect_pages(request, kind, 1, ACTOR, daemon_epoch=2)
                cursor = 2 if kind == "rust-route-plans" else 1
                self.assertEqual(request.call_args_list, [
                    ((0, None),), ((cursor, 2),), ((0, None),), ((cursor, 3),)])
                self.assertEqual(result["epoch"], 3)
                if kind == "rust-route-plans":
                    self.assertEqual(len(result["frames"]), 3)
                    self.assertNotIn("|500|", "\n".join(result["frames"]))
                else:
                    self.assertEqual(result["executed_slice"], [0, 1])

    def test_plan_collection_echoes_epoch_and_preserves_sealed_frames(self):
        calls = []
        replies = iter((PAGE_ZERO, PAGE_LAST))

        def request(cursor, epoch):
            calls.append(collectors.daemon_plan_request(1, cursor, 16, epoch))
            return next(replies)

        result = collectors.collect_pages(request, "rust-route-plans", 1, ACTOR)
        self.assertEqual(calls, ["RUST_ROUTE_PLANS 1 0 16", "RUST_ROUTE_PLANS 1 2 16 2"])
        self.assertEqual(result["epoch"], 2)
        self.assertEqual(routing_evidence.parse_plan(result["frames"], 1, ACTOR),
            ("0123456789abcdef", 2000, ((0, 500, 0, 100), (0, 500, 0, 100))))

    def test_client_collection_echoes_epoch(self):
        calls = []
        replies = iter((CLIENT_ZERO, CLIENT_LAST))

        def request(cursor, epoch):
            calls.append(collectors.client_page_request(
                BRIDGE_CORR, "rust-route-client", 1, ACTOR, cursor, 1, epoch))
            return next(replies)

        result = collectors.collect_pages(request, "rust-route-client", 1, ACTOR)
        self.assertEqual(result["executed_slice"], [0, 1])
        self.assertEqual(calls[-1], f"SACS_RECORDER|{BRIDGE_CORR}|rust-route-client|1|{ACTOR}|1|1|2")

    def test_refusals_restart_at_page_zero_and_discard_old_frames(self):
        for refusal in (REQUIRED, MISMATCH):
            for kind, first, last, payload in (
                ("rust-route-plans", PAGE_ZERO, PAGE_LAST, "frames"),
                ("rust-route-client", CLIENT_ZERO, CLIENT_LAST, "executed_slice"),
            ):
                with self.subTest(refusal=refusal, kind=kind):
                    request = Mock(side_effect=[
                        first, refusal,
                        first.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                        last.replace(b"epoch=2", b"epoch=3").replace(b"|500|", b"|700|"),
                    ])
                    result = collectors.collect_pages(request, kind, 1, ACTOR)
                    cursor = 2 if kind == "rust-route-plans" else 1
                    self.assertEqual(request.call_args_list, [
                        ((0, None),), ((cursor, 2),), ((0, None),), ((cursor, 3),)])
                    self.assertEqual(result["epoch"], 3)
                    if payload == "frames":
                        self.assertNotIn("|500|", "\n".join(result[payload]))
                        self.assertEqual(len(result[payload]), 3)
                    else:
                        self.assertEqual(result[payload], [0, 1])

    def test_different_epoch_page_is_never_spliced(self):
        for kind, first, last in (
            ("rust-route-plans", PAGE_ZERO, PAGE_LAST),
            ("rust-route-client", CLIENT_ZERO, CLIENT_LAST),
        ):
            with self.subTest(kind=kind):
                request = Mock(side_effect=[
                    first, last.replace(b"epoch=2", b"epoch=3"),
                    first.replace(b"epoch=2", b"epoch=3"),
                    last.replace(b"epoch=2", b"epoch=3"),
                ])
                result = collectors.collect_pages(request, kind, 1, ACTOR)
                self.assertEqual(result["epoch"], 3)
                self.assertEqual(request.call_args_list[2], ((0, None),))

    def test_repeated_epoch_changes_fail_closed(self):
        request = Mock(side_effect=[PAGE_ZERO, MISMATCH, PAGE_ZERO, MISMATCH])
        with self.assertRaises(collectors.PageRefused):
            collectors.collect_pages(request, "rust-route-plans", 1, ACTOR)
        self.assertEqual(request.call_count, 4)

    def test_other_refusals_do_not_restart(self):
        request = Mock(return_value=b"rust-route-client=missing")
        with self.assertRaises(collectors.PageRefused):
            collectors.collect_pages(request, "rust-route-plans", 1, ACTOR)
        self.assertEqual(request.call_count, 1)

    def test_wrong_identity_cursor_and_changed_total_are_blocked(self):
        for bad in (
            PAGE_LAST.replace(b"corr=1", b"corr=2"),
            PAGE_LAST.replace(str(ACTOR).encode(), b"10"),
            PAGE_LAST.replace(b"cursor=2;count=1;total=3;next=3;more=0",
                              b"cursor=1;count=1;total=3;next=2;more=1"),
            PAGE_LAST.replace(b"total=3;next=3;more=0", b"total=4;next=3;more=1"),
        ):
            request = Mock(side_effect=[PAGE_ZERO, bad])
            with self.subTest(bad=bad[:100]):
                with self.assertRaises(routing_evidence.Missing):
                    collectors.collect_pages(request, "rust-route-plans", 1, ACTOR)

    def test_client_evidence_older_than_daemon_is_blocked(self):
        request = Mock(return_value=CLIENT_ZERO.replace(b"count=1;next=1;more=1",
            b"count=2;next=2;more=0").replace(b"executed_slice=[0]", b"executed_slice=[0,1]"))
        with self.assertRaises(routing_evidence.Missing):
            collectors.collect_pages(request, "rust-route-client", 1, ACTOR, daemon_epoch=3)
        self.assertEqual(request.call_count, 1)


if __name__ == "__main__":
    unittest.main()
