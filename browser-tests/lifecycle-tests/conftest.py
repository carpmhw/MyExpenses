from __future__ import annotations

import sys
from pathlib import Path


# 讓獨立生命週期測試可以載入 browser-tests 根目錄的程序管理 helper。
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
