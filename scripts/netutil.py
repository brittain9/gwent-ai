"""Network helpers shared by the scripts that start local servers."""

import socket


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]
