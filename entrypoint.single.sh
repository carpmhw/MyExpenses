#!/bin/sh
set -eu

# 單容器 image 預設只提供 HTTP；HTTPS 部署可用環境變數覆寫為 true。
: "${Auth__CookieSecure:=false}"
export Auth__CookieSecure

dotnet /app/MyExpenses.Api.dll &
backend_pid="$!"

nginx -g 'daemon off;' &
nginx_pid="$!"

# 處理容器停止訊號，讓 backend 與 nginx 都能被正常關閉。
term_handler() {
    kill "$backend_pid" "$nginx_pid" 2>/dev/null || true
    wait "$backend_pid" "$nginx_pid" 2>/dev/null || true
}

trap term_handler INT TERM

while :; do
    if ! kill -0 "$backend_pid" 2>/dev/null; then
        wait "$backend_pid"
        exit "$?"
    fi

    if ! kill -0 "$nginx_pid" 2>/dev/null; then
        wait "$nginx_pid"
        exit "$?"
    fi

    sleep 1
done
