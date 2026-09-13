from __future__ import annotations

import errno
import os
import re
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import IO, Mapping, Sequence
from urllib.parse import urlsplit


_ANSI_ESCAPE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
_LOCAL_URL = re.compile(r"\bLocal:\s*(https?://[^\s]+)")


# 表示測試伺服器生命週期操作失敗。
class ServerLifecycleError(RuntimeError):
    """測試伺服器生命週期的基礎例外。"""


# 表示測試伺服器無法啟動或未能在期限內就緒。
class ServerStartupError(ServerLifecycleError):
    """測試伺服器啟動失敗。"""


# 表示測試伺服器程序群組未能在期限內完整回收。
class ServerCleanupError(ServerLifecycleError):
    """測試伺服器清理失敗。"""


# 管理一個具有獨立 POSIX 程序群組的測試伺服器。
class ManagedServer:
    # 建立尚未啟動的測試伺服器管理器。
    def __init__(
        self,
        command: Sequence[str],
        cwd: Path,
        host: str,
        port: int,
        health_path: str = "/",
        startup_timeout: float = 30.0,
        graceful_timeout: float = 5.0,
        log_tail_limit: int = 8_000,
        env: Mapping[str, str] | None = None,
    ) -> None:
        self.command = tuple(command)
        self.cwd = Path(cwd)
        self.host = host
        self.port = port
        self.health_path = health_path if health_path.startswith("/") else f"/{health_path}"
        self.startup_timeout = startup_timeout
        self.graceful_timeout = graceful_timeout
        self.log_tail_limit = log_tail_limit
        self.env = dict(env) if env is not None else None
        self._process: subprocess.Popen[bytes] | None = None
        self._pgid: int | None = None
        self._log_directory: TemporaryDirectory[str] | None = None
        self._log_path: Path | None = None
        self._log_handle: IO[str] | None = None
        self._log_offset = 0
        self._startup_buffer = ""
        self._cleanup_complete = False

    # 取得實際啟動的直接程序，供測試驗證父程序先退出的情境。
    @property
    def process(self) -> subprocess.Popen[bytes]:
        if self._process is None:
            raise ServerLifecycleError("server has not been started")
        return self._process

    # 取得本次伺服器的直接程序 PID。
    @property
    def pid(self) -> int:
        return self.process.pid

    # 取得本次伺服器獨立程序群組的 PGID。
    @property
    def pgid(self) -> int:
        if self._pgid is None:
            raise ServerLifecycleError("server has not been started")
        return self._pgid

    # 取得本次伺服器輸出日誌的暫存路徑。
    @property
    def log_path(self) -> Path | None:
        return self._log_path

    # 啟動伺服器，並依要求等待本次程序完成就緒握手。
    def start(self, wait_for_ready: bool = True) -> ManagedServer:
        self._assert_posix_support()
        if self._process is not None and not self._cleanup_complete:
            raise ServerLifecycleError("server has already been started")
        self._assert_port_available()
        self._create_log()
        try:
            self._process = subprocess.Popen(
                list(self.command),
                cwd=self.cwd,
                env=self.env,
                stdout=self._log_handle,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            self._pgid = self._process.pid
            self._cleanup_complete = False
            if wait_for_ready:
                self._wait_until_ready()
            return self
        except BaseException as error:
            try:
                self.stop()
            except BaseException as cleanup_error:
                self._add_note(error, f"server cleanup also failed: {cleanup_error}")
            raise

    # 進入 context manager 並啟動已配置的測試伺服器。
    def __enter__(self) -> ManagedServer:
        return self.start()

    # 離開 context manager 並保留原始測試例外與清理例外。
    def __exit__(self, exc_type: object, exc_value: BaseException | None, traceback: object) -> bool:
        try:
            self.stop()
        except BaseException as cleanup_error:
            if exc_value is not None:
                self._add_note(exc_value, f"server cleanup also failed: {cleanup_error}")
                return False
            raise
        return False

    # 以有界期限終止本次程序群組並關閉日誌資源。
    def stop(self) -> None:
        if self._cleanup_complete:
            return
        cleanup_error: BaseException | None = None
        try:
            if self._pgid is not None:
                self._terminate_process_group()
        except BaseException as error:
            cleanup_error = error
        finally:
            self._close_log()
            self._remove_log_directory()
        if cleanup_error is not None:
            raise cleanup_error
        self._cleanup_complete = True

    # 確認目前平台具備本設計需要的 POSIX 程序群組控制能力。
    def _assert_posix_support(self) -> None:
        if os.name != "posix" or not hasattr(os, "killpg"):
            raise ServerStartupError("browser test server lifecycle requires POSIX process groups")

    # 在啟動前確認指定 host 與 port 沒有被其他 listener 占用。
    def _assert_port_available(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                probe.bind((self.host, self.port))
            except OSError as error:
                if error.errno in {errno.EADDRINUSE, errno.EACCES}:
                    raise ServerStartupError(
                        f"server port {self.host}:{self.port} is already in use"
                    ) from error
                raise ServerStartupError(
                    f"cannot reserve server port {self.host}:{self.port}: {error}"
                ) from error

    # 建立不依賴 PIPE 的 session 暫存日誌檔案。
    def _create_log(self) -> None:
        self._log_directory = tempfile.TemporaryDirectory(prefix="myexpenses-server-")
        self._log_path = Path(self._log_directory.name) / "server.log"
        self._log_handle = self._log_path.open("w", encoding="utf-8", buffering=1)
        self._log_offset = 0
        self._startup_buffer = ""

    # 等待本次程序的 Local 訊號、程序存活與健康 URL 同時成立。
    def _wait_until_ready(self) -> None:
        deadline = time.monotonic() + self.startup_timeout
        startup_seen = False
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise ServerStartupError(self._startup_failure("server exited before readiness"))
            self._startup_buffer = (self._startup_buffer + self._read_new_log())[-8_192:]
            if not startup_seen:
                startup_seen = self._has_expected_local_url(self._startup_buffer)
            if startup_seen:
                if self.process.poll() is not None:
                    raise ServerStartupError(self._startup_failure("server exited after Local signal"))
                if self._health_check():
                    if self.process.poll() is None:
                        return
                    raise ServerStartupError(self._startup_failure("server exited during readiness"))
            time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))
        raise ServerStartupError(self._startup_failure("server did not become ready before timeout"))

    # 讀取子程序自上次輪詢後新增的日誌內容。
    def _read_new_log(self) -> str:
        if self._log_path is None:
            return ""
        try:
            with self._log_path.open("r", encoding="utf-8", errors="replace") as log_file:
                log_file.seek(self._log_offset)
                chunk = log_file.read()
                self._log_offset = log_file.tell()
                return chunk
        except FileNotFoundError:
            return ""

    # 判斷日誌中的 Local URL 是否精確指向本次 host 與 port。
    def _has_expected_local_url(self, output: str) -> bool:
        clean_output = _ANSI_ESCAPE.sub("", output)
        for match in _LOCAL_URL.finditer(clean_output):
            try:
                parsed = urlsplit(match.group(1).rstrip("/"))
                if (
                    parsed.scheme in {"http", "https"}
                    and parsed.hostname == self.host
                    and parsed.port == self.port
                    and parsed.path in {"", "/"}
                ):
                    return True
            except ValueError:
                continue
        return False

    # 以短 timeout 檢查本次伺服器的健康 URL 是否回傳 HTTP 200。
    def _health_check(self) -> bool:
        url = f"http://{self.host}:{self.port}{self.health_path}"
        try:
            with urllib.request.urlopen(url, timeout=0.5) as response:
                return response.status == 200
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, OSError):
            return False

    # 取得有限長度的日誌尾段，避免錯誤診斷無界增長。
    def _read_log_tail(self) -> str:
        if self._log_path is None:
            return "<no server log available>"
        try:
            output = self._log_path.read_text(encoding="utf-8", errors="replace")
        except FileNotFoundError:
            return "<server log was not created>"
        return output[-self.log_tail_limit:]

    # 組合包含 URL、exit code 與日誌尾段的啟動錯誤診斷。
    def _startup_failure(self, reason: str) -> str:
        return (
            f"{reason}; url=http://{self.host}:{self.port}{self.health_path}; "
            f"exit_code={self.process.poll()!r}; log_tail=\n{self._read_log_tail()}"
        )

    # 判斷本次程序群組目前是否仍存在。
    def _process_group_exists(self) -> bool:
        try:
            os.killpg(self.pgid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True

    # 對本次程序群組送出指定 signal，忽略群組已自行消失的競態。
    def _signal_process_group(self, sig: signal.Signals) -> None:
        try:
            os.killpg(self.pgid, sig)
        except ProcessLookupError:
            return

    # 在 deadline 前等待整個程序群組消失。
    def _wait_for_group_exit(self, deadline: float) -> bool:
        while time.monotonic() < deadline:
            self.process.poll()
            if not self._process_group_exists():
                return True
            time.sleep(0.02)
        self.process.poll()
        return not self._process_group_exists()

    # 等待直接 Popen 程序被目前測試程序 wait 回收。
    def _wait_for_direct_process(self, deadline: float) -> None:
        while self.process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.02)
        if self.process.poll() is None:
            raise ServerCleanupError(
                f"direct server process {self.pid} did not exit; pgid={self.pgid}"
            )
        self.process.wait()

    # 執行 SIGTERM、SIGKILL 兩階段的完整程序群組清理。
    def _terminate_process_group(self) -> None:
        if not self._process_group_exists():
            self._wait_for_direct_process(time.monotonic() + self.graceful_timeout)
            return
        self._signal_process_group(signal.SIGTERM)
        graceful_deadline = time.monotonic() + self.graceful_timeout
        if not self._wait_for_group_exit(graceful_deadline):
            self._signal_process_group(signal.SIGKILL)
            forced_deadline = time.monotonic() + self.graceful_timeout
            if not self._wait_for_group_exit(forced_deadline):
                raise ServerCleanupError(
                    f"process group {self.pgid} remained alive after SIGKILL; "
                    f"log_tail=\n{self._read_log_tail()}"
                )
        self._wait_for_direct_process(time.monotonic() + self.graceful_timeout)

    # 關閉父程序持有的日誌檔案描述元。
    def _close_log(self) -> None:
        if self._log_handle is not None:
            self._log_handle.close()
            self._log_handle = None

    # 清除 session 暫存日誌目錄。
    def _remove_log_directory(self) -> None:
        if self._log_directory is not None:
            self._log_directory.cleanup()
            self._log_directory = None

    # 將清理診斷附加到原始例外而不覆蓋原始失敗。
    @staticmethod
    def _add_note(error: BaseException, note: str) -> None:
        add_note = getattr(error, "add_note", None)
        if add_note is not None:
            add_note(note)


# 建立 frontend E2E 使用的固定 Vite 啟動命令，拒絕自動換埠。
def build_vite_command(host: str, port: int) -> list[str]:
    return [
        "npm",
        "run",
        "dev",
        "--",
        "--host",
        host,
        "--port",
        str(port),
        "--strictPort",
    ]


# 建立使用固定 host、port 與 /login 就緒檢查的 Vite 管理器。
def create_vite_server(
    frontend: Path,
    host: str = "127.0.0.1",
    port: int = 5199,
    startup_timeout: float = 30.0,
) -> ManagedServer:
    return ManagedServer(
        command=build_vite_command(host, port),
        cwd=frontend,
        host=host,
        port=port,
        health_path="/login",
        startup_timeout=startup_timeout,
    )
