"""Loopback defaults shared by the dev stack and its local tools."""

import os

PUBLIC_ENDPOINT = os.environ.get("SIMSONLINE_PUBLIC_ENDPOINT") or "127.0.0.1:34019"
SERVER_BRIDGECTL = os.environ.get("SIMSONLINE_SERVER_BRIDGECTL") or "127.0.0.1:34029"
CLIENT_BRIDGECTL = os.environ.get("SIMSONLINE_CLIENT_BRIDGECTL") or "127.0.0.1:34030"
