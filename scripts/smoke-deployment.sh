#!/bin/sh
set -eu
umask 077

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
DOCKER_BIN=${DOCKER_BIN:-docker}
COMPOSE_FILE_PATH="$ROOT_DIR/docker-compose.yml"
TEMP_DIR=''
ENV_FILE=''
PROJECT_NAME=''
CLEANUP_PROJECT=0
BASE_URL=''
HTTP_PORT=''
SMOKE_SKIP_BUILD=${SMOKE_SKIP_BUILD:-0}
SMOKE_REMOTE_LIVE=${SMOKE_REMOTE_LIVE:-0}
SMOKE_REMOTE_INSECURE=${SMOKE_REMOTE_INSECURE:-0}
SMOKE_HEALTH_ATTEMPTS=${SMOKE_HEALTH_ATTEMPTS:-90}
SMOKE_HEALTH_DELAY=${SMOKE_HEALTH_DELAY:-2}

# 顯示 smoke script 的用法與不會暴露 secret 的可選設定。
usage() {
    cat <<'EOF'
Usage: scripts/smoke-deployment.sh <local|lan|remote>

local  Start an isolated two-container Compose deployment and verify setup,
       health, proxy routing, authentication, private backend, and restart.
lan    Render an explicit non-loopback LAN configuration and verify the
       trusted-network HTTP warning contract without binding a public network.
remote Render an HTTPS/Secure-cookie/trusted-proxy configuration and verify
       the edge contract statically. Set SMOKE_REMOTE_LIVE=1 to run a local
       edge simulation with X-Forwarded-Proto headers.

Environment:
  COMPOSE_PROJECT_NAME  Explicit isolated project name; it must be unused.
  SMOKE_SKIP_BUILD=1    Reuse existing images for local or live remote checks.
  SMOKE_HTTP_PORT       Host port for a live check (default is process-derived).
  SMOKE_REMOTE_EDGE_URL HTTPS origin for optional external-edge header checks.
  SMOKE_REMOTE_HTTP_EDGE_URL HTTP origin for optional redirect checks.
  SMOKE_REMOTE_INSECURE=1  Allow self-signed certificates for an external edge.
  SMOKE_HEALTH_ATTEMPTS / SMOKE_HEALTH_DELAY  Live wait tuning.

The script creates ephemeral secrets in memory, never prints them, and cleans
only the isolated Compose project and its volumes.
EOF
}

# 顯示不含 secret 的錯誤並以失敗狀態結束。
fail() {
    printf 'deployment smoke failed: %s\n' "$1" >&2
    exit 1
}

# 驗證外部指令存在，讓缺少 prerequisite 的錯誤保持明確。
require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        fail "missing prerequisite: $1"
    fi
}

# 透過固定 Compose 檔案執行命令，避免呼叫端的 COMPOSE_FILE 改變測試範圍。
compose() {
    unset COMPOSE_FILE
    "$DOCKER_BIN" compose "$@"
}

# 驗證整數設定，避免 shell arithmetic 接受未預期的輸入。
validate_integer() {
    integer_name=$1
    integer_value=$2
    case "$integer_value" in
        ''|*[!0-9]*)
            fail "$integer_name must be a decimal integer"
            ;;
    esac
}

# 驗證 host port 與等待次數落在可用範圍內。
validate_runtime_settings() {
    validate_integer SMOKE_HTTP_PORT "$HTTP_PORT"
    if [ "$HTTP_PORT" -lt 1 ] || [ "$HTTP_PORT" -gt 65535 ]; then
        fail 'SMOKE_HTTP_PORT must be between 1 and 65535'
    fi

    validate_integer SMOKE_HEALTH_ATTEMPTS "$SMOKE_HEALTH_ATTEMPTS"
    if [ "$SMOKE_HEALTH_ATTEMPTS" -lt 1 ]; then
        fail 'SMOKE_HEALTH_ATTEMPTS must be greater than zero'
    fi

    validate_integer SMOKE_HEALTH_DELAY "$SMOKE_HEALTH_DELAY"
    if [ "$SMOKE_HEALTH_DELAY" -lt 1 ]; then
        fail 'SMOKE_HEALTH_DELAY must be greater than zero'
    fi

    case "$SMOKE_SKIP_BUILD" in
        0|1) ;;
        *) fail 'SMOKE_SKIP_BUILD must be 0 or 1' ;;
    esac
    case "$SMOKE_REMOTE_LIVE" in
        0|1) ;;
        *) fail 'SMOKE_REMOTE_LIVE must be 0 or 1' ;;
    esac
    case "$SMOKE_REMOTE_INSECURE" in
        0|1) ;;
        *) fail 'SMOKE_REMOTE_INSECURE must be 0 or 1' ;;
    esac
}

# 產生只在本次 process 使用的高熵 secret，呼叫端不應直接列印回傳值。
generate_secret() {
    generated_secret=$(openssl rand -hex 32) || fail 'openssl could not generate an ephemeral secret'
    [ -n "$generated_secret" ] || fail 'openssl returned an empty ephemeral secret'
    printf '%s' "$generated_secret"
}

# 產生符合帳號密碼政策且不含 JSON 特殊字元的 smoke owner password。
generate_owner_password() {
    password_suffix=$(openssl rand -hex 16) || fail 'openssl could not generate an owner password'
    printf 'Smoke-%s!' "$password_suffix"
}

# 驗證 Compose project name，避免 cleanup 觸及含糊或既有的 project。
validate_project_name() {
    case "$PROJECT_NAME" in
        ''|*[!a-z0-9_-]*) fail 'COMPOSE_PROJECT_NAME must use lowercase letters, digits, hyphen, or underscore' ;;
    esac
    case "$PROJECT_NAME" in
        [a-z0-9]*) ;;
        *) fail 'COMPOSE_PROJECT_NAME must start with a lowercase letter or digit' ;;
    esac
    if [ "${#PROJECT_NAME}" -gt 63 ]; then
        fail 'COMPOSE_PROJECT_NAME is too long'
    fi
}

# 驗證指定 project 沒有既有資源，保護使用者的 volumes 與 containers。
assert_project_is_fresh() {
    existing_containers=$($DOCKER_BIN ps -aq --filter "label=com.docker.compose.project=$PROJECT_NAME" 2>/dev/null) || \
        fail 'Docker daemon is unavailable while checking project isolation'
    existing_volumes=$($DOCKER_BIN volume ls -q --filter "name=${PROJECT_NAME}_" 2>/dev/null) || \
        fail 'Docker volume listing failed while checking project isolation'
    existing_networks=$($DOCKER_BIN network ls -q --filter "name=${PROJECT_NAME}_" 2>/dev/null) || \
        fail 'Docker network listing failed while checking project isolation'

    [ -z "$existing_containers" ] || fail 'Compose project name already has containers; choose an unused project name'
    [ -z "$existing_volumes" ] || fail 'Compose project name already has volumes; choose an unused project name'
    [ -z "$existing_networks" ] || fail 'Compose project name already has networks; choose an unused project name'
}

# 只清理本次建立的 isolated Compose project，並保留 cleanup failure 的狀態。
smoke_cleanup() {
    smoke_status=$?
    trap - 0 INT TERM
    if [ "$CLEANUP_PROJECT" -eq 1 ]; then
        if ! compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" --env-file "$ENV_FILE" \
            down --volumes --remove-orphans >/dev/null 2>&1; then
            printf 'deployment smoke cleanup failed for the isolated Compose project\n' >&2
            [ "$smoke_status" -ne 0 ] || smoke_status=1
        fi
    fi
    if [ -n "$TEMP_DIR" ]; then
        rm -rf "$TEMP_DIR"
    fi
    exit "$smoke_status"
}

# 將部署模式與其安全相關設定寫入本次 process 的環境。
set_mode_environment() {
    mode_name=$1
    bind_address=$2
    public_origin=$3
    secure_cookies=$4

    export MYEXPENSES_DEPLOYMENT_MODE="$mode_name"
    export MYEXPENSES_BIND_ADDRESS="$bind_address"
    export MYEXPENSES_PUBLIC_ORIGIN="$public_origin"
    export MYEXPENSES_COOKIE_SECURE="$secure_cookies"
    export MYEXPENSES_HTTP_PORT="$HTTP_PORT"

    case "$mode_name" in
        Remote)
            export Deployment__TrustedNetworks__0='172.16.0.0/12'
            export MYEXPENSES_TRUSTED_EDGE_NETWORKS='172.16.0.0/12'
            unset Deployment__TrustedProxies__0
            ;;
        *)
            export MYEXPENSES_TRUSTED_EDGE_NETWORKS='127.0.0.1/32'
            unset Deployment__TrustedProxies__0
            unset Deployment__TrustedNetworks__0
            ;;
    esac
}

# 驗證 Compose 缺少任一 operator secret 時會 fail closed，且不把 log 印出。
preflight_secrets() {
    preflight_log="$TEMP_DIR/preflight-jwt.log"
    if (unset MYEXPENSES_JWT_SECRET; compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" \
        --env-file "$ENV_FILE" config >"$preflight_log" 2>&1); then
        fail 'Compose accepted a missing MYEXPENSES_JWT_SECRET'
    fi
    grep -Fq -- 'MYEXPENSES_JWT_SECRET must be provided' "$preflight_log" || \
        fail 'Compose did not report the missing JWT secret contract'

}

# 將目前不含可列印輸出的 Compose rendering 寫入 owner-only temporary file。
render_compose() {
    render_file=$1
    if ! compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" --env-file "$ENV_FILE" \
        config >"$render_file" 2>"$TEMP_DIR/compose-config.log"; then
        fail 'docker compose config failed for the requested deployment mode'
    fi
}

# 驗證 rendered Compose 包含指定的非敏感契約片段。
assert_rendered_contains() {
    rendered_file=$1
    rendered_value=$2
    grep -Fq -- "$rendered_value" "$rendered_file" || \
        fail "rendered Compose is missing a required deployment contract: $rendered_value"
}

# 驗證 rendered Compose 的指定 service 沒有 host port publication。
assert_service_has_no_ports() {
    rendered_file=$1
    service_name=$2
    if awk -v service="$service_name" '
        $0 == "  " service ":" { in_service = 1; next }
        in_service && /^  [^[:space:]]/ { in_service = 0 }
        in_service && /^[[:space:]]+ports:/ { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$rendered_file"; then
        fail "rendered Compose publishes a host port for $service_name"
    fi
}

# 驗證檔案含有部署契約文字，避免 smoke script 與 operator 文件脫節。
assert_file_contains() {
    contract_file=$1
    contract_value=$2
    grep -Fq -- "$contract_value" "$contract_file" || \
        fail "$contract_file is missing the documented deployment contract"
}

# 驗證 container 對外沒有透過 Compose 發布 backend 5000。
assert_backend_isolated() {
    backend_container=$(compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" \
        --env-file "$ENV_FILE" ps -q backend 2>/dev/null) || \
        fail 'cannot locate backend container for host-port isolation check'
    [ -n "$backend_container" ] || fail 'backend container is missing for host-port isolation check'
    backend_ports=$($DOCKER_BIN inspect --format '{{json .NetworkSettings.Ports}}' \
        "$backend_container" 2>/dev/null) || fail 'cannot inspect backend host-port bindings'
    case "$backend_ports" in
        *'"5000/tcp":null'*) ;;
        *) fail 'backend port 5000 is published to the host' ;;
    esac
}

# 將 owner setup 與 login request 寫入權限受限的暫存檔，避免 credential 出現在 argv。
prepare_owner_payloads() {
    OWNER_EMAIL="smoke-owner-$$@example.invalid"
    OWNER_DISPLAY_NAME='Smoke Owner'
    OWNER_PASSWORD=$(generate_owner_password)
    printf '{"email":"%s","displayName":"%s","password":"%s"}\n' \
        "$OWNER_EMAIL" "$OWNER_DISPLAY_NAME" "$OWNER_PASSWORD" >"$TEMP_DIR/register.json"
    printf '{"email":"%s","password":"%s"}\n' \
        "$OWNER_EMAIL" "$OWNER_PASSWORD" >"$TEMP_DIR/login.json"
    printf 'X-MyExpenses-Bootstrap-Secret: %s\n' "$MYEXPENSES_BOOTSTRAP_SECRET" \
        >"$TEMP_DIR/bootstrap.header"
}

# 從 JSON response 取出 JWT 並寫入 temporary header，避免 token 出現在 shell argv 或輸出。
extract_bearer_token() {
    token_response=$1
    extracted_token=$(awk -F '"token":"' \
        'NF > 1 { split($2, token_parts, /"/); print token_parts[1]; exit }' \
        "$token_response")
    [ -n "$extracted_token" ] || fail 'authenticated response did not contain a JWT token'
    printf '%s' "$extracted_token"
}

# 發送 HTTP request 並只回傳 status code，response body 保留在 owner-only 暫存檔。
request_http_status() {
    request_output=$1
    shift
    curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --output "$request_output" --write-out '%{http_code}' "$@"
}

# 等待 reverse proxy 的指定 health endpoint 成功，並可模擬 TLS edge 的 forwarded scheme。
wait_for_endpoint() {
    wait_base_url=$1
    wait_path=$2
    wait_forwarded_proto=${3:-}
    wait_attempt=1
    while [ "$wait_attempt" -le "$SMOKE_HEALTH_ATTEMPTS" ]; do
        if [ -n "$wait_forwarded_proto" ]; then
            wait_status=$(curl --fail --silent --show-error --connect-timeout 3 --max-time 10 \
                --header "X-Forwarded-Proto: $wait_forwarded_proto" \
                --output /dev/null --write-out '%{http_code}' "$wait_base_url$wait_path" 2>/dev/null || true)
            if [ "$wait_status" = '200' ]; then
                return 0
            fi
        else
            wait_status=$(curl --fail --silent --show-error --connect-timeout 3 --max-time 10 \
                --output /dev/null --write-out '%{http_code}' "$wait_base_url$wait_path" 2>/dev/null || true)
            if [ "$wait_status" = '200' ]; then
                return 0
            fi
        fi
        wait_attempt=$((wait_attempt + 1))
        sleep "$SMOKE_HEALTH_DELAY"
    done
    fail "timed out waiting for $wait_path"
}

# 驗證 Compose service 的 healthcheck 已達到 healthy 狀態。
assert_container_healthy() {
    healthy_service=$1
    healthy_attempt=1
    while [ "$healthy_attempt" -le "$SMOKE_HEALTH_ATTEMPTS" ]; do
        healthy_container=$(compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" \
            --env-file "$ENV_FILE" ps -q "$healthy_service" 2>/dev/null) || \
            fail "cannot locate Compose service $healthy_service"
        [ -n "$healthy_container" ] || fail "Compose service $healthy_service has no container"
        healthy_status=$($DOCKER_BIN inspect --format \
            '{{if .State.Health}}{{.State.Health.Status}}{{else}}no-healthcheck{{end}}' \
            "$healthy_container" 2>/dev/null) || fail "cannot inspect Compose service $healthy_service"
        case "$healthy_status" in
            healthy)
                return 0
                ;;
            no-healthcheck)
                fail "Compose service $healthy_service has no healthcheck"
                ;;
        esac
        healthy_attempt=$((healthy_attempt + 1))
        sleep "$SMOKE_HEALTH_DELAY"
    done
    fail "Compose service $healthy_service did not become healthy"
}

# 驗證 HTTP response body 含有指定的非敏感 JSON 欄位。
assert_response_contains() {
    response_file=$1
    response_pattern=$2
    grep -Eq -- "$response_pattern" "$response_file" || \
        fail 'HTTP response did not contain the expected deployment contract'
}

# 驗證 response header 存在，且不列印可能包含 cookie value 的 header file。
assert_header_present() {
    header_file=$1
    header_name=$2
    grep -Eiq -- "^${header_name}:" "$header_file" || \
        fail "HTTP response is missing the $header_name security header"
}

# 驗證 remote edge 的 redirect、security headers、forwarded scheme 與 Secure cookie。
verify_remote_local_edge() {
    redirect_headers="$TEMP_DIR/remote-redirect.headers"
    redirect_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'X-Forwarded-Proto: http' --dump-header "$redirect_headers" \
        --output /dev/null --write-out '%{http_code}' "$BASE_URL/health/live") || \
        fail 'remote HTTP redirect check could not reach the reverse proxy'
    case "$redirect_status" in
        301|302|307|308) ;;
        *) fail 'remote HTTP traffic was not redirected or rejected' ;;
    esac
    grep -Eiq '^Location:[[:space:]]*https://' "$redirect_headers" || \
        fail 'remote HTTP response did not point to an HTTPS origin'

    https_headers="$TEMP_DIR/remote-https.headers"
    https_body="$TEMP_DIR/remote-https.body"
    https_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'X-Forwarded-Proto: https' --dump-header "$https_headers" \
        --output "$https_body" --write-out '%{http_code}' "$BASE_URL/health/ready") || \
        fail 'remote HTTPS edge simulation could not reach readiness'
    [ "$https_status" = '200' ] || fail 'remote HTTPS edge simulation did not return readiness'
    assert_header_present "$https_headers" 'Strict-Transport-Security'
    assert_header_present "$https_headers" 'Content-Security-Policy'
    assert_header_present "$https_headers" 'X-Content-Type-Options'
    assert_header_present "$https_headers" 'Referrer-Policy'

    remote_cookie="$TEMP_DIR/remote.cookies"
    remote_register_headers="$TEMP_DIR/remote-register.headers"
    remote_register_body="$TEMP_DIR/remote-register.body"
    remote_register_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'Content-Type: application/json' \
        --header 'X-Forwarded-Proto: https' \
        --header "@$TEMP_DIR/bootstrap.header" \
        --cookie-jar "$remote_cookie" --dump-header "$remote_register_headers" \
        --data-binary "@$TEMP_DIR/register.json" \
        --output "$remote_register_body" --write-out '%{http_code}' \
        "$BASE_URL/api/auth/register") || fail 'remote owner setup request failed'
    [ "$remote_register_status" = '200' ] || fail 'remote owner setup did not authenticate successfully'
    grep -Eiq 'Set-Cookie:.*;[[:space:]]*Secure([;[:space:]]|$)' "$remote_register_headers" || \
        fail 'remote session cookie is missing the Secure attribute'
    remote_token=$(extract_bearer_token "$remote_register_body")
    printf 'Authorization: Bearer %s\n' "$remote_token" >"$TEMP_DIR/remote-authorization.header"
    remote_auth_body="$TEMP_DIR/remote-auth.body"
    remote_auth_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'X-Forwarded-Proto: https' \
        --header "@$TEMP_DIR/remote-authorization.header" --cookie "$remote_cookie" \
        --output "$remote_auth_body" --write-out '%{http_code}' \
        "$BASE_URL/api/auth/status") || fail 'remote authenticated status request failed'
    [ "$remote_auth_status" = '200' ] || fail 'remote authenticated status returned an unexpected status'
    assert_response_contains "$remote_auth_body" '"authenticated"[[:space:]]*:[[:space:]]*true'
}

# 驗證可選的外部 HTTPS edge，不要求真實 certificate 或 public internet。
verify_remote_external_edge() {
    [ -n "${SMOKE_REMOTE_EDGE_URL:-}" ] || return 0
    case "$SMOKE_REMOTE_EDGE_URL" in
        https://*) ;;
        *) fail 'SMOKE_REMOTE_EDGE_URL must use https:// and contain only an origin' ;;
    esac
    case "$SMOKE_REMOTE_EDGE_URL" in
        https://*/*) fail 'SMOKE_REMOTE_EDGE_URL must not contain a path' ;;
    esac

    external_headers="$TEMP_DIR/external-remote.headers"
    if [ "$SMOKE_REMOTE_INSECURE" -eq 1 ]; then
        external_status=$(curl --insecure --silent --show-error --connect-timeout 5 --max-time 20 \
            --dump-header "$external_headers" --output /dev/null --write-out '%{http_code}' \
            "$SMOKE_REMOTE_EDGE_URL/health/ready") || fail 'external HTTPS edge request failed'
    else
        external_status=$(curl --silent --show-error --connect-timeout 5 --max-time 20 \
            --dump-header "$external_headers" --output /dev/null --write-out '%{http_code}' \
            "$SMOKE_REMOTE_EDGE_URL/health/ready") || fail 'external HTTPS edge request failed'
    fi
    [ "$external_status" = '200' ] || fail 'external HTTPS edge did not return readiness'
    assert_header_present "$external_headers" 'Strict-Transport-Security'
    assert_header_present "$external_headers" 'Content-Security-Policy'
    assert_header_present "$external_headers" 'X-Content-Type-Options'
    assert_header_present "$external_headers" 'Referrer-Policy'

    if [ -n "${SMOKE_REMOTE_HTTP_EDGE_URL:-}" ]; then
        case "$SMOKE_REMOTE_HTTP_EDGE_URL" in
            http://*) ;;
            *) fail 'SMOKE_REMOTE_HTTP_EDGE_URL must use http://' ;;
        esac
        case "$SMOKE_REMOTE_HTTP_EDGE_URL" in
            http://*/*) fail 'SMOKE_REMOTE_HTTP_EDGE_URL must not contain a path' ;;
        esac
        external_http_headers="$TEMP_DIR/external-remote-http.headers"
        external_http_status=$(curl --silent --show-error --connect-timeout 5 --max-time 20 \
            --dump-header "$external_http_headers" --output /dev/null --write-out '%{http_code}' \
            "$SMOKE_REMOTE_HTTP_EDGE_URL/health/ready") || fail 'external HTTP edge request failed'
        case "$external_http_status" in
            301|302|307|308) ;;
            *) fail 'external HTTP edge did not redirect or reject traffic' ;;
        esac
        grep -Eiq '^Location:[[:space:]]*https://' "$external_http_headers" || \
            fail 'external HTTP edge did not advertise an HTTPS location'
    fi
}

# 啟動 isolated Compose stack；未指定 skip build 時重新建立 deployment images。
start_stack() {
    assert_project_is_fresh
    CLEANUP_PROJECT=1
    if [ "$SMOKE_SKIP_BUILD" -eq 1 ]; then
        compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" --env-file "$ENV_FILE" up -d || \
            fail 'docker compose up failed while reusing existing images'
    else
        compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" --env-file "$ENV_FILE" up -d --build || \
            fail 'docker compose up --build failed'
    fi
}

# 執行 Local 的完整 fresh-install、authentication、restart 與 network isolation smoke。
run_local() {
    BASE_URL="http://127.0.0.1:$HTTP_PORT"
    set_mode_environment Local 127.0.0.1 "$BASE_URL" false
    preflight_secrets
    local_render="$TEMP_DIR/local.compose.yml"
    render_compose "$local_render"
    assert_rendered_contains "$local_render" 'host_ip: 127.0.0.1'
    assert_rendered_contains "$local_render" 'Deployment__Mode: Local'
    assert_rendered_contains "$local_render" 'Auth__CookieSecure: "false"'
    assert_service_has_no_ports "$local_render" backend

    if ! "$DOCKER_BIN" info >/dev/null 2>&1; then
        fail 'Docker daemon is unavailable for the Local live smoke test'
    fi
    start_stack
    wait_for_endpoint "$BASE_URL" /health/live
    wait_for_endpoint "$BASE_URL" /health/ready
    assert_container_healthy backend
    assert_container_healthy frontend
    assert_backend_isolated

    status_body="$TEMP_DIR/local-status-before.body"
    status_code=$(request_http_status "$status_body" "$BASE_URL/api/auth/status") || \
        fail 'reverse-proxy /api routing check failed'
    [ "$status_code" = '200' ] || fail 'reverse-proxy /api/auth/status returned an unexpected status'
    assert_response_contains "$status_body" '"hasUsers"[[:space:]]*:[[:space:]]*false'

    prepare_owner_payloads
    local_cookie="$TEMP_DIR/local.cookies"
    local_register_headers="$TEMP_DIR/local-register.headers"
    local_register_body="$TEMP_DIR/local-register.body"
    register_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'Content-Type: application/json' \
        --header "@$TEMP_DIR/bootstrap.header" \
        --cookie-jar "$local_cookie" --dump-header "$local_register_headers" \
        --data-binary "@$TEMP_DIR/register.json" \
        --output "$local_register_body" --write-out '%{http_code}' \
        "$BASE_URL/api/auth/register") || fail 'Local owner setup request failed'
    [ "$register_status" = '200' ] || fail 'Local owner setup did not return an authenticated response'
    assert_response_contains "$local_register_body" '"token"[[:space:]]*:'
    if grep -Eiq 'Set-Cookie:.*;[[:space:]]*Secure([;[:space:]]|$)' "$local_register_headers"; then
        fail 'Local HTTP session cookie unexpectedly has the Secure attribute'
    fi
    registration_token=$(extract_bearer_token "$local_register_body")
    printf 'Authorization: Bearer %s\n' "$registration_token" >"$TEMP_DIR/authorization.header"

    authenticated_body="$TEMP_DIR/local-authenticated.body"
    authenticated_status=$(request_http_status "$authenticated_body" \
        --header "@$TEMP_DIR/authorization.header" --cookie "$local_cookie" \
        "$BASE_URL/api/auth/status") || \
        fail 'Local authenticated status request failed'
    [ "$authenticated_status" = '200' ] || fail 'Local authenticated status returned an unexpected status'
    assert_response_contains "$authenticated_body" '"authenticated"[[:space:]]*:[[:space:]]*true'
    assert_response_contains "$authenticated_body" '"hasUsers"[[:space:]]*:[[:space:]]*true'

    compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE_PATH" --env-file "$ENV_FILE" restart >/dev/null || \
        fail 'docker compose restart failed'
    wait_for_endpoint "$BASE_URL" /health/live
    wait_for_endpoint "$BASE_URL" /health/ready
    assert_container_healthy backend
    assert_container_healthy frontend

    persisted_body="$TEMP_DIR/local-persisted.body"
    persisted_status=$(request_http_status "$persisted_body" \
        --header "@$TEMP_DIR/authorization.header" --cookie "$local_cookie" \
        "$BASE_URL/api/auth/status") || \
        fail 'restart persistence authentication request failed'
    [ "$persisted_status" = '200' ] || fail 'restart persistence status returned an unexpected status'
    assert_response_contains "$persisted_body" '"authenticated"[[:space:]]*:[[:space:]]*true'
    assert_response_contains "$persisted_body" '"hasUsers"[[:space:]]*:[[:space:]]*true'

    login_cookie="$TEMP_DIR/login.cookies"
    login_body="$TEMP_DIR/login.body"
    login_status=$(curl --silent --show-error --connect-timeout 3 --max-time 15 \
        --header 'Content-Type: application/json' --cookie-jar "$login_cookie" \
        --data-binary "@$TEMP_DIR/login.json" --output "$login_body" \
        --write-out '%{http_code}' "$BASE_URL/api/auth/login") || fail 'Local owner login request failed'
    [ "$login_status" = '200' ] || fail 'Local owner login did not return success after restart'
    assert_response_contains "$login_body" '"token"[[:space:]]*:'

    printf 'local deployment smoke passed\n'
}

# 驗證 LAN 的明確 non-loopback bind、HTTP cookie 設定與 trusted-network 警告文件。
run_lan() {
    set_mode_environment Lan 192.0.2.20 'http://192.0.2.20' false
    preflight_secrets
    lan_render="$TEMP_DIR/lan.compose.yml"
    render_compose "$lan_render"
    assert_rendered_contains "$lan_render" 'host_ip: 192.0.2.20'
    assert_rendered_contains "$lan_render" 'Deployment__Mode: Lan'
    assert_rendered_contains "$lan_render" 'Deployment__PublicOrigin: http://192.0.2.20'
    assert_rendered_contains "$lan_render" 'Auth__CookieSecure: "false"'
    assert_service_has_no_ports "$lan_render" backend
    assert_file_contains "$ROOT_DIR/README.md" 'Plain HTTP'
    assert_file_contains "$ROOT_DIR/README.md" 'home network'
    assert_file_contains "$ROOT_DIR/README.md" 'internet'
    assert_file_contains "$ROOT_DIR/frontend/entrypoint.sh" 'Lan|LAN|lan'
    printf 'warning: LAN mode uses plain HTTP and is for a trusted home network only; do not expose it to the internet\n' >&2
    printf 'lan deployment render checks passed\n'
}

# 驗證 Remote 的 HTTPS origin、Secure cookie、trusted network 與 browser headers 契約。
run_remote() {
    set_mode_environment Remote 127.0.0.1 'https://expenses.example.test' true
    preflight_secrets
    remote_render="$TEMP_DIR/remote.compose.yml"
    render_compose "$remote_render"
    assert_rendered_contains "$remote_render" 'host_ip: 127.0.0.1'
    assert_rendered_contains "$remote_render" 'Deployment__Mode: Remote'
    assert_rendered_contains "$remote_render" 'Deployment__PublicOrigin: https://expenses.example.test'
    assert_rendered_contains "$remote_render" 'Auth__CookieSecure: "true"'
    assert_rendered_contains "$remote_render" 'Deployment__TrustedNetworks__0: 172.16.0.0/12'
    assert_service_has_no_ports "$remote_render" backend

    assert_file_contains "$ROOT_DIR/README.md" 'HTTP 必須 redirect 到 HTTPS 或被拒絕'
    assert_file_contains "$ROOT_DIR/README.md" 'MYEXPENSES_COOKIE_SECURE=true'
    assert_file_contains "$ROOT_DIR/README.md" 'Deployment__TrustedProxies__0'
    assert_file_contains "$ROOT_DIR/frontend/entrypoint.sh" '    default 1;'
    assert_file_contains "$ROOT_DIR/frontend/entrypoint.sh" '    https 0;'
    assert_file_contains "$ROOT_DIR/frontend/nginx.conf" 'return 301 https://$host$request_uri;'
    assert_file_contains "$ROOT_DIR/frontend/nginx.conf" 'Strict-Transport-Security'
    assert_file_contains "$ROOT_DIR/frontend/nginx.conf" 'Content-Security-Policy'
    assert_file_contains "$ROOT_DIR/frontend/nginx.conf" 'X-Content-Type-Options'
    assert_file_contains "$ROOT_DIR/frontend/nginx.conf" 'Referrer-Policy'

    if [ "$SMOKE_REMOTE_LIVE" -eq 1 ]; then
        if ! "$DOCKER_BIN" info >/dev/null 2>&1; then
            fail 'Docker daemon is unavailable for the Remote edge simulation'
        fi
        BASE_URL="http://127.0.0.1:$HTTP_PORT"
        prepare_owner_payloads
        start_stack
        wait_for_endpoint "$BASE_URL" /health/live https
        wait_for_endpoint "$BASE_URL" /health/ready https
        assert_container_healthy backend
        assert_container_healthy frontend
        verify_remote_local_edge
    fi

    verify_remote_external_edge
    printf 'remote deployment validation passed\n'
}

# 驗證 prerequisite、建立 isolated temporary state，並 dispatch 到指定部署模式。
main() {
    if [ "$#" -ne 1 ]; then
        usage >&2
        exit 2
    fi
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        local|lan|remote) ;;
        *)
            usage >&2
            fail "unsupported deployment mode: $1"
            ;;
    esac

    [ -f "$COMPOSE_FILE_PATH" ] || fail 'docker-compose.yml is missing'
    require_command "$DOCKER_BIN"
    require_command curl
    require_command grep
    require_command awk
    require_command mktemp
    require_command openssl
    require_command sleep
    if ! "$DOCKER_BIN" compose version >/dev/null 2>&1; then
        fail 'Docker Compose v2 is required'
    fi

    TEMP_DIR=$(mktemp -d "${TMPDIR:-/tmp}/myexpenses-smoke.XXXXXX") || \
        fail 'could not create an owner-only temporary directory'
    ENV_FILE="$TEMP_DIR/empty.env"
    : >"$ENV_FILE"
    trap smoke_cleanup 0
    trap 'exit 130' INT TERM

    if [ -n "${COMPOSE_PROJECT_NAME:-}" ]; then
        PROJECT_NAME=$COMPOSE_PROJECT_NAME
    else
        PROJECT_NAME="myexpenses-smoke-$$"
    fi
    validate_project_name

    if [ -n "${SMOKE_HTTP_PORT:-}" ]; then
        HTTP_PORT=$SMOKE_HTTP_PORT
    else
        HTTP_PORT=$((18080 + ($$ % 1000)))
    fi
    validate_runtime_settings

    export MYEXPENSES_JWT_SECRET=$(generate_secret)
    export MYEXPENSES_BOOTSTRAP_SECRET=$(generate_secret)

    case "$1" in
        local) run_local ;;
        lan) run_lan ;;
        remote) run_remote ;;
    esac
}

main "$@"
