#!/bin/sh
set -eu

backend_pid=''
nginx_pid=''

# 依 deployment mode 產生 HTTP/HTTPS redirect policy，避免 Remote 預設服務明文流量。
configure_nginx_mode() {
    mode_config='/etc/nginx/conf.d/00-myexpenses-mode.conf'
    mode=${MYEXPENSES_DEPLOYMENT_MODE:-Local}
    trusted_edge_networks="127.0.0.1/32 ${MYEXPENSES_TRUSTED_EDGE_NETWORKS:-}"
    emitted_networks=' '

    {
        printf '%s\n' 'geo $myexpenses_trusted_edge {' '    default 0;'
        for edge_network in $(printf '%s' "$trusted_edge_networks" | tr ',' ' '); do
            case "$edge_network" in
                ''|*[!0-9A-Fa-f:./]*)
                    printf 'invalid trusted edge network: %s\n' "$edge_network" >&2
                    exit 1
                    ;;
            esac
            # 預設 loopback 與自訂清單可能重複，避免 nginx geo 重複網路警告。
            case "$emitted_networks" in
                *" $edge_network "*) continue ;;
            esac
            emitted_networks="$emitted_networks$edge_network "
            printf '    %s 1;\n' "$edge_network"
        done
        printf '%s\n' '}'
    } > "$mode_config"

    case "$mode" in
        Local|local|Lan|LAN|lan)
            printf '%s\n' \
                'map $myexpenses_forwarded_proto $myexpenses_redirect_http {' \
                '    default 0;' \
                '}' >> "$mode_config"
            ;;
        Remote|REMOTE|remote)
            printf '%s\n' \
                'map $myexpenses_forwarded_proto $myexpenses_redirect_http {' \
                '    default 1;' \
                '    https 0;' \
                '}' >> "$mode_config"
            ;;
        *)
            printf 'unsupported deployment mode: %s\n' "$mode" >&2
            exit 1
            ;;
    esac
}

# 等待 backend readiness，避免 nginx 在 startup 尚未完成時接受流量。
wait_for_backend() {
    attempts=0
    while [ "$attempts" -lt 60 ]; do
        backend_health_status=$(curl --fail --silent --show-error --max-time 2 \
            -H 'X-Forwarded-Proto: https' -o /dev/null -w '%{http_code}' \
            http://127.0.0.1:5000/health/ready 2>/dev/null || true)
        if [ "$backend_health_status" = '200' ]; then
            return 0
        fi

        if ! kill -0 "$backend_pid" 2>/dev/null; then
            backend_status=0
            wait "$backend_pid" || backend_status=$?
            exit "$backend_status"
        fi

        attempts=$((attempts + 1))
        sleep 1
    done

    printf '%s\n' 'backend readiness check timed out' >&2
    kill "$backend_pid" 2>/dev/null || true
    backend_status=0
    wait "$backend_pid" || backend_status=$?
    exit 1
}

# 處理容器停止訊號，讓 backend 與 nginx 都能被正常關閉。
term_handler() {
    if [ -n "$backend_pid" ]; then
        kill "$backend_pid" 2>/dev/null || true
    fi
    if [ -n "$nginx_pid" ]; then
        kill "$nginx_pid" 2>/dev/null || true
    fi
    wait "$backend_pid" 2>/dev/null || true
    wait "$nginx_pid" 2>/dev/null || true
}

trap term_handler INT TERM

# 在啟動 backend 前先套用 nginx 的 deployment mode 設定。
configure_nginx_mode

# 先啟動 backend 並確認 readiness，再讓 nginx 成為對外入口。
dotnet /app/MyExpenses.Api.dll &
backend_pid="$!"
wait_for_backend

nginx -g 'daemon off;' &
nginx_pid="$!"

while :; do
    if ! kill -0 "$backend_pid" 2>/dev/null; then
        backend_status=0
        wait "$backend_pid" || backend_status=$?
        exit "$backend_status"
    fi

    if ! kill -0 "$nginx_pid" 2>/dev/null; then
        nginx_status=0
        wait "$nginx_pid" || nginx_status=$?
        exit "$nginx_status"
    fi

    sleep 1
done
