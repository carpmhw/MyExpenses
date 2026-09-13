from __future__ import annotations

import socket
import urllib.request
from pathlib import Path

import pytest

from server_lifecycle import ServerStartupError, create_vite_server


ROOT = Path(__file__).resolve().parents[2]
FRONTEND = ROOT / "frontend"


# 取得尚未使用的本機 TCP port，避免真實 Vite 測試與固定 E2E port 互相干擾。
def _unused_port() -> int:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        return int(reservation.getsockname()[1])


# 取得外部 listener 使用的 port，驗證 fixture 不會終止未知服務。
def _external_listener() -> tuple[socket.socket, int]:
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    return listener, int(listener.getsockname()[1])


# 啟動目前安裝版本的 Vite，驗證 Local URL、HTTP readiness 與群組清理。
def test_current_vite_starts_with_expected_local_url() -> None:
    port = _unused_port()
    server = create_vite_server(FRONTEND, port=port, startup_timeout=30.0)
    with server:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/login", timeout=2) as response:
            assert response.status == 200
        assert server.process.poll() is None
    assert server.process.poll() is not None


# 驗證第二個真實 Vite session 不會換埠或重用第一個 session。
def test_current_vite_strict_port_rejects_second_server() -> None:
    port = _unused_port()
    with create_vite_server(FRONTEND, port=port, startup_timeout=30.0):
        second = create_vite_server(FRONTEND, port=port, startup_timeout=3.0)
        with pytest.raises(ServerStartupError, match="already in use"):
            second.start()


# 驗證固定 port 被未知 listener 占用時，未知 listener 仍維持可用。
def test_current_vite_does_not_terminate_external_listener() -> None:
    listener, port = _external_listener()
    try:
        with pytest.raises(ServerStartupError, match="already in use"):
            create_vite_server(FRONTEND, port=port, startup_timeout=3.0).start()
        assert listener.fileno() >= 0
    finally:
        listener.close()
