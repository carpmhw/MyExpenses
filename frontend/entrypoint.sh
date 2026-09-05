#!/bin/sh
set -eu

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

configure_nginx_mode
exec nginx -g 'daemon off;'
