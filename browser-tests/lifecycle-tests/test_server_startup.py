from __future__ import annotations

import socket
import subprocess
import sys
import textwrap
import time
from pathlib import Path
from typing import Any

import pytest

import server_lifecycle
from server_lifecycle import ManagedServer, ServerStartupError, build_vite_command


# 產生可選擇 Local 訊號格式的 HTTP 測試伺服器程式。
def _server_script(
    port: int,
    displayed_port: int | None = None,
    fragmented_output: bool = False,
    output_bytes: int = 0,
) -> str:
    shown_port = displayed_port if displayed_port is not None else port
    output = (
        f"sys.stdout.write('  Local: http://127.0.0.1:{shown_port}/\\n'); sys.stdout.flush()"
        if not fragmented_output
        else (
            f"sys.stdout.write('  Lo'); sys.stdout.flush(); time.sleep(0.02); "
            f"sys.stdout.write('cal:   http://127.0.0.1:{shown_port}/\\x1b[0m\\n'); "
            "sys.stdout.flush()"
        )
    )
    noise = f"sys.stdout.write('x' * {output_bytes}); sys.stdout.flush();" if output_bytes else ""
    return textwrap.dedent(
        f"""
        import http.server
        import sys
        import time

        # 回應啟動判定測試的 HTTP 健康檢查。
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'ok')

            # 隱藏測試伺服器的請求日誌，避免干擾 Local 啟動訊號解析。
            def log_message(self, *args):
                pass

        server = http.server.ThreadingHTTPServer(('127.0.0.1', {port}), Handler)
        {noise}
        {output}
        server.serve_forever()
        """
    )


# 建立使用指定啟動輸出的 ManagedServer。
def _managed_server(
    port: int,
    displayed_port: int | None = None,
    fragmented_output: bool = False,
    output_bytes: int = 0,
) -> ManagedServer:
    return ManagedServer(
        command=[
            sys.executable,
            "-c",
            _server_script(port, displayed_port, fragmented_output, output_bytes),
        ],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        health_path="/login",
        startup_timeout=0.5,
        graceful_timeout=0.1,
    )


# 取得尚未使用的本機 TCP port，讓啟動測試彼此隔離。
def _unused_port() -> int:
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        return int(reservation.getsockname()[1])


# 產生會因 strict port 被占用而失敗的啟動程式。
def _strict_port_script(port: int) -> str:
    return textwrap.dedent(
        f"""
        import socket
        import sys
        import time

        listener = socket.socket()
        try:
            listener.bind(('127.0.0.1', {port}))
        except OSError:
            print('Port {port} is already in use', flush=True)
            raise SystemExit(1)
        print('  Local: http://127.0.0.1:{port}/', flush=True)
        time.sleep(30)
        """
    )


# 驗證正式 Vite 命令明確包含 strictPort，不會自動換埠。
def test_vite_command_uses_strict_port() -> None:
    command = build_vite_command("127.0.0.1", 5199)
    assert command[-1] == "--strictPort"
    assert command[-2:] == ["5199", "--strictPort"]


# 驗證錯誤的 Local URL 即使目標 port 回應 HTTP 200 也不能通過就緒。
def test_wrong_local_url_cannot_become_ready_from_target_http() -> None:
    port = _unused_port()
    server = _managed_server(port, displayed_port=port + 1)
    with pytest.raises(ServerStartupError, match="did not become ready"):
        server.start()


# 驗證 ANSI 控制碼與分段 Local 輸出仍能被正確解析。
def test_fragmented_ansi_local_output_is_ready() -> None:
    port = _unused_port()
    with _managed_server(port, fragmented_output=True):
        pass


# 驗證大量 stdout 不會填滿 PIPE 而阻塞啟動或清理。
def test_large_startup_output_does_not_block() -> None:
    port = _unused_port()
    with _managed_server(port, output_bytes=1_000_000):
        pass


# 驗證沒有 Local 訊號的長時間程序會在 deadline 到期後被清理。
def test_startup_timeout_cleans_process_group() -> None:
    port = _unused_port()
    server = ManagedServer(
        command=[sys.executable, "-c", "import time; print('not ready', flush=True); time.sleep(30)"],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        startup_timeout=0.2,
        graceful_timeout=0.1,
    )
    with pytest.raises(ServerStartupError, match="did not become ready"):
        server.start()
    assert server.process.poll() is not None


# 驗證啟動預檢後被搶占 port 時，strict-port 程序會失敗而不接受外部服務。
def test_port_taken_after_preflight_is_not_accepted(monkeypatch: pytest.MonkeyPatch) -> None:
    port = _unused_port()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    original_popen = server_lifecycle.subprocess.Popen

    # 在 Popen 與子程序綁定 port 之間插入外部 listener，重現競態。
    def racing_popen(*args: Any, **kwargs: Any) -> subprocess.Popen[Any]:
        listener.bind(("127.0.0.1", port))
        listener.listen()
        return original_popen(*args, **kwargs)

    monkeypatch.setattr(server_lifecycle.subprocess, "Popen", racing_popen)
    server = ManagedServer(
        command=[sys.executable, "-c", _strict_port_script(port)],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        startup_timeout=1.0,
        graceful_timeout=0.1,
    )
    try:
        with pytest.raises(ServerStartupError, match="exited before readiness"):
            server.start()
        assert listener.fileno() >= 0
    finally:
        listener.close()


# 驗證 Popen 建立失敗時暫存日誌仍會被關閉與刪除。
def test_popen_failure_closes_log_resource() -> None:
    port = _unused_port()
    server = ManagedServer(
        command=["/path/that/does/not/exist/myexpenses-server"],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        startup_timeout=0.2,
        graceful_timeout=0.1,
    )
    with pytest.raises(FileNotFoundError):
        server.start()
    assert server.log_path is not None
    assert not server.log_path.exists()


# 驗證啟動與清理同時失敗時會保留兩種診斷，且原始錯誤仍為主例外。
def test_startup_and_cleanup_errors_are_both_retained(monkeypatch: pytest.MonkeyPatch) -> None:
    port = _unused_port()
    server = ManagedServer(
        command=["/path/that/does/not/exist/myexpenses-server"],
        cwd=Path.cwd(),
        host="127.0.0.1",
        port=port,
        startup_timeout=0.2,
        graceful_timeout=0.1,
    )
    original_stop = server.stop

    # 在保留原始資源清理的同時模擬清理階段額外失敗。
    def stop_with_secondary_error() -> None:
        original_stop()
        raise RuntimeError("cleanup boom")

    monkeypatch.setattr(server, "stop", stop_with_secondary_error)
    with pytest.raises(FileNotFoundError) as error:
        server.start()
    assert any("cleanup also failed: cleanup boom" in note for note in error.value.__notes__)
