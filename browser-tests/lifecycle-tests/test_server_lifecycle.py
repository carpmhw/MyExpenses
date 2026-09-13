from __future__ import annotations

import os
import signal
import socket
import subprocess
import sys
import time
import textwrap
from typing import Any
from pathlib import Path

import pytest

import server_lifecycle
from server_lifecycle import ManagedServer, ServerStartupError


# 判斷程序是否仍存在，讓生命週期測試直接驗證作業系統資源狀態。
def _pid_exists(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


# 等待指定 PID 出現或消失，避免測試以固定 sleep 猜測程序狀態。
def _wait_for_pid(pid: int, expected: bool, timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if _pid_exists(pid) == expected:
            return
        time.sleep(0.02)
    assert _pid_exists(pid) == expected


# 等待指定 TCP port 可重新綁定，確認程序清理確實釋放 socket。
def _wait_for_port_release(host: str, port: int, timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                probe.bind((host, port))
            except OSError:
                time.sleep(0.02)
                continue
            return
    raise AssertionError(f"port {host}:{port} was not released")


# 終止測試建立但尚未由目標生命週期管理器接管的程序。
def _terminate_process(process: subprocess.Popen[Any]) -> None:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=1)


# 產生能輸出 Vite 風格 Local 訊號並提供 HTTP 200 的測試伺服器程式。
def _server_script(port: int, ignore_sigterm: bool = False) -> str:
    ignore = "signal.signal(signal.SIGTERM, signal.SIG_IGN)" if ignore_sigterm else ""
    return textwrap.dedent(
        f"""
        import http.server
        import signal

        {ignore}

        # 回應生命週期測試的 HTTP 健康檢查。
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'ok')

            # 隱藏測試伺服器的請求日誌，避免干擾 Local 啟動訊號解析。
            def log_message(self, *args):
                pass

        server = http.server.ThreadingHTTPServer(('127.0.0.1', {port}), Handler)
        print('  Local: http://127.0.0.1:{port}/', flush=True)
        server.serve_forever()
        """
    )


# 產生會先啟動伺服器子程序、再自行退出的父程序程式。
def _parent_exits_after_child_script(port: int) -> str:
    child = repr(_server_script(port))
    return (
        "import subprocess, sys, time; "
        f"subprocess.Popen([sys.executable, '-c', {child}]); "
        "time.sleep(0.8)"
    )


# 建立使用 Python 測試伺服器的 ManagedServer 設定。
def _managed_server(port: int, ignore_sigterm: bool = False) -> ManagedServer:
    return ManagedServer(
        command=[sys.executable, "-c", _server_script(port, ignore_sigterm)],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        health_path="/login",
        startup_timeout=5.0,
        graceful_timeout=0.2,
    )


# 等待 ManagedServer 的直接程序由 Popen poll 回收，避免把 zombie 誤判為存活。
def _wait_for_process_exit(process: subprocess.Popen[Any], timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            return
        time.sleep(0.02)
    assert process.poll() is not None


# 驗證每次伺服器都建立獨立程序群組，且 teardown 會釋放實際 port。
def test_server_uses_isolated_process_group_and_releases_port() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = _managed_server(port)
    with server:
        assert server.pgid == server.pid
        assert server.pgid != os.getpgrp()
        assert _pid_exists(server.pid)
    _wait_for_port_release("127.0.0.1", port)


# 驗證直接父程序先退出時，生命週期管理器仍會回收同群組的伺服器子程序。
def test_parent_exit_does_not_skip_descendant_cleanup() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = ManagedServer(
        command=[sys.executable, "-c", _parent_exits_after_child_script(port)],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        health_path="/login",
        startup_timeout=5.0,
        graceful_timeout=0.2,
    )
    with server:
        _wait_for_process_exit(server.process, timeout=3.0)
    _wait_for_port_release("127.0.0.1", port)


# 驗證 SIGTERM 被忽略時會升級為 SIGKILL，仍能在期限內完成清理。
def test_cleanup_escalates_when_child_ignores_sigterm() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = _managed_server(port, ignore_sigterm=True)
    pid = 0
    with server:
        pid = server.pid
    _wait_for_process_exit(server.process)
    assert not _pid_exists(pid)
    _wait_for_port_release("127.0.0.1", port)


# 驗證固定 port 被外部 HTTP 服務占用時，不會誤認外部回應為本次啟動成功。
def test_existing_http_listener_is_not_reused() -> None:
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    port = int(listener.getsockname()[1])
    try:
        with pytest.raises(ServerStartupError, match="already in use"):
            _managed_server(port).start()
        assert listener.fileno() >= 0
    finally:
        listener.close()


# 驗證清理測試自身建立的外部程序，避免回歸測試留下新的孤兒。
def test_cleanup_helper_is_safe_for_unmanaged_process() -> None:
    process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    try:
        _wait_for_pid(process.pid, True)
    finally:
        _terminate_process(process)
        _wait_for_pid(process.pid, False)


# 驗證重複呼叫 stop 不會再次操作已經消失的程序群組。
def test_stop_is_idempotent() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = _managed_server(port).start()
    server.stop()
    server.stop()
    _wait_for_port_release("127.0.0.1", port)


# 驗證測試例外仍會保留，且 context manager 仍會回收伺服器。
def test_context_cleanup_preserves_test_exception() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = _managed_server(port)
    with pytest.raises(KeyboardInterrupt):
        with server:
            raise KeyboardInterrupt()
    _wait_for_port_release("127.0.0.1", port)


# 驗證啟動程序提前退出時錯誤包含 exit code 與日誌尾段。
def test_startup_failure_keeps_exit_code_and_log_tail() -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = ManagedServer(
        command=[sys.executable, "-c", "print('startup boom', flush=True); raise SystemExit(7)"],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        startup_timeout=2.0,
        graceful_timeout=0.2,
    )
    with pytest.raises(ServerStartupError, match=r"(?s)exit_code=7.*startup boom"):
        server.start()


# 驗證不支援 POSIX 程序群組的平台會在建立子程序前失敗。
def test_unsupported_platform_is_rejected_before_spawn(monkeypatch: pytest.MonkeyPatch) -> None:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = int(reservation.getsockname()[1])
    server = _managed_server(port)
    monkeypatch.setattr(server_lifecycle.os, "name", "nt")
    with pytest.raises(ServerStartupError, match="requires POSIX"):
        server.start()
