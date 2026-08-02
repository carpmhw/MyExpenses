#!/bin/sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

# 顯示測試失敗原因並以失敗狀態結束。
fail() {
    printf 'deployment config test failed: %s\n' "$1" >&2
    exit 1
}

# 驗證檔案包含指定的固定設定片段。
assert_contains() {
    file=$1
    expected=$2
    grep -Fq -- "$expected" "$ROOT_DIR/$file" || \
        fail "$file 缺少設定片段: $expected"
}

# 驗證檔案不包含不可重用的設定片段。
assert_not_contains() {
    file=$1
    unexpected=$2
    if grep -Fq -- "$unexpected" "$ROOT_DIR/$file"; then
        fail "$file 不應包含設定片段: $unexpected"
    fi
}

# 驗證指定 Compose service 區段沒有直接發布 host port。
assert_service_has_no_ports() {
    file=$1
    service=$2
    if awk -v service="$service" '
        $0 == "  " service ":" { in_service = 1; next }
        in_service && /^  [^[:space:]]/ { in_service = 0 }
        in_service && /^[[:space:]]+ports:/ { found = 1 }
        END { exit found ? 0 : 1 }
    ' "$ROOT_DIR/$file"; then
        fail "$file 的 $service service 不應發布 host port"
    fi
}

# 驗證 Compose 在缺少 operator secret 時會 fail fast。
assert_compose_requires_secrets() {
    file=$1
    command -v docker >/dev/null 2>&1 || return 0
    docker compose version >/dev/null 2>&1 || return 0

    if env -u MYEXPENSES_JWT_SECRET \
        docker compose -f "$ROOT_DIR/$file" config >/dev/null 2>&1; then
        fail "$file 在缺少 operator secrets 時仍可通過 compose config"
    fi
}

# 驗證 initialized installation 可在移除 bootstrap secret 後繼續 render 與啟動。
assert_compose_allows_missing_bootstrap_secret() {
    file=$1
    command -v docker >/dev/null 2>&1 || return 0
    docker compose version >/dev/null 2>&1 || return 0

    MYEXPENSES_JWT_SECRET='test-jwt-secret-that-is-longer-than-32-characters' \
        env -u MYEXPENSES_BOOTSTRAP_SECRET \
        docker compose -f "$ROOT_DIR/$file" config >/dev/null || \
        fail "$file 在移除 initialized bootstrap secret 後仍無法通過 compose config"
}

# 驗證提供測試 secret 後 Compose 檔案可以被解析。
assert_compose_is_valid() {
    file=$1
    command -v docker >/dev/null 2>&1 || return 0
    docker compose version >/dev/null 2>&1 || return 0

    MYEXPENSES_JWT_SECRET='test-jwt-secret-that-is-longer-than-32-characters' \
    MYEXPENSES_BOOTSTRAP_SECRET='test-bootstrap-secret-that-is-longer-than-32-characters' \
        docker compose -f "$ROOT_DIR/$file" config >/dev/null || \
        fail "$file 在提供測試 secret 後無法通過 compose config"
}

# 驗證 operator 覆寫 LAN/Remote bind 與 cookie 設定時不會被 deployment default 蓋回。
assert_compose_overrides_are_preserved() {
    file=$1
    command -v docker >/dev/null 2>&1 || return 0
    docker compose version >/dev/null 2>&1 || return 0

    rendered=$(MYEXPENSES_JWT_SECRET='test-jwt-secret-that-is-longer-than-32-characters' \
        MYEXPENSES_BOOTSTRAP_SECRET='test-bootstrap-secret-that-is-longer-than-32-characters' \
        MYEXPENSES_BIND_ADDRESS='192.0.2.10' \
        MYEXPENSES_HTTP_PORT='8080' \
        MYEXPENSES_DEPLOYMENT_MODE='Remote' \
        MYEXPENSES_PUBLIC_ORIGIN='https://expenses.example.com' \
        MYEXPENSES_COOKIE_SECURE='true' \
        Deployment__TrustedNetworks__0='192.0.2.0/24' \
        docker compose -f "$ROOT_DIR/$file" config) || \
        fail "$file 的 operator override 無法通過 compose config"

    printf '%s\n' "$rendered" | grep -Fq 'host_ip: 192.0.2.10' || \
        fail "$file 未保留 operator bind address override"
    printf '%s\n' "$rendered" | grep -Fq 'published: "8080"' || \
        fail "$file 未保留 operator HTTP port override"
    printf '%s\n' "$rendered" | grep -Fq 'Deployment__Mode: Remote' || \
        fail "$file 未保留 operator deployment mode override"
    printf '%s\n' "$rendered" | grep -Fq 'Deployment__PublicOrigin: https://expenses.example.com' || \
        fail "$file 未保留 operator public origin override"
    printf '%s\n' "$rendered" | grep -Fq 'Deployment__TrustedNetworks__0: 192.0.2.0/24' || \
        fail "$file 未保留 operator trusted network override"
    printf '%s\n' "$rendered" | grep -Fq 'Auth__CookieSecure: "true"' || \
        fail "$file 未保留 operator cookie security override"
}

# 驗證所有 deployment boundary 檔案沒有已知可重用的 Production secret。
assert_no_reusable_production_secrets() {
    for file in \
        docker-compose.yml \
        docker-compose.single.yml \
        Dockerfile.single \
        backend/Dockerfile \
        frontend/Dockerfile \
        frontend/entrypoint.sh \
        frontend/nginx-mode.conf \
        entrypoint.single.sh \
        nginx.single.conf \
        frontend/nginx.conf; do
        assert_not_contains "$file" '0d8b1f1298e341e6a22fef3751d86e3a'
        assert_not_contains "$file" 'change-this-to-a-secure-random-key-at-least-32-characters'
        assert_not_contains "$file" 'placeholder-key-replace-in-production'
    done
}

# 執行 Compose、網路邊界、health proxy 與 secret 檢查。
main() {
    cd "$ROOT_DIR"

    assert_contains docker-compose.yml 'Jwt__Secret=${MYEXPENSES_JWT_SECRET:?'
    assert_contains docker-compose.yml 'Bootstrap__Secret=${MYEXPENSES_BOOTSTRAP_SECRET:-}'
    assert_contains docker-compose.yml '127.0.0.1}:${MYEXPENSES_HTTP_PORT:-80}:80'
    assert_contains docker-compose.yml 'Deployment__Mode=${MYEXPENSES_DEPLOYMENT_MODE:-Local}'
    assert_contains docker-compose.yml 'Deployment__BindAddress=${MYEXPENSES_BIND_ADDRESS:-127.0.0.1}'
    assert_contains docker-compose.yml 'MYEXPENSES_DEPLOYMENT_MODE: "${MYEXPENSES_DEPLOYMENT_MODE:-Local}"'
    assert_contains docker-compose.yml 'Deployment__TrustedProxies__0'
    assert_contains docker-compose.yml 'Deployment__TrustedNetworks__0'
    assert_contains docker-compose.yml 'myexpenses-dataprotection:/app/keys'
    assert_contains docker-compose.yml 'myexpenses-backups:/app/data/backups'
    assert_contains docker-compose.yml 'condition: service_healthy'
    assert_contains docker-compose.yml 'MYEXPENSES_TRUSTED_EDGE_NETWORKS'
    assert_contains docker-compose.yml '/health/ready'
    assert_contains docker-compose.yml '--header=X-Forwarded-Proto: https'
    assert_not_contains docker-compose.yml '5000:5000'
    assert_service_has_no_ports docker-compose.yml backend

    assert_contains docker-compose.single.yml 'Jwt__Secret=${MYEXPENSES_JWT_SECRET:?'
    assert_contains docker-compose.single.yml 'Bootstrap__Secret=${MYEXPENSES_BOOTSTRAP_SECRET:-}'
    assert_contains docker-compose.single.yml '127.0.0.1}:${MYEXPENSES_HTTP_PORT:-80}:80'
    assert_contains docker-compose.single.yml 'Deployment__Mode=${MYEXPENSES_DEPLOYMENT_MODE:-Local}'
    assert_contains docker-compose.single.yml 'Deployment__BindAddress=${MYEXPENSES_BIND_ADDRESS:-127.0.0.1}'
    assert_contains docker-compose.single.yml 'MYEXPENSES_DEPLOYMENT_MODE=${MYEXPENSES_DEPLOYMENT_MODE:-Local}'
    assert_contains docker-compose.single.yml 'Deployment__TrustedProxies__0'
    assert_contains docker-compose.single.yml 'Deployment__TrustedNetworks__0'
    assert_contains docker-compose.single.yml 'myexpenses-dataprotection:/app/keys'
    assert_contains docker-compose.single.yml 'MYEXPENSES_TRUSTED_EDGE_NETWORKS'
    assert_contains docker-compose.single.yml 'myexpenses-backups:/app/data/backups'
    assert_not_contains docker-compose.single.yml '5000:5000'
    assert_contains docker-compose.single.yml 'X-Forwarded-Proto: https'

    assert_contains frontend/nginx.conf 'location = /health/live'
    assert_contains frontend/nginx.conf 'location = /health/ready'
    assert_contains frontend/nginx.conf 'proxy_pass http://backend:5000/health/ready;'
    assert_contains frontend/nginx.conf 'X-Forwarded-Proto $myexpenses_forwarded_proto'
    assert_contains frontend/nginx.conf 'myexpenses_trusted_edge:$http_x_forwarded_proto'
    assert_contains frontend/entrypoint.sh 'MYEXPENSES_TRUSTED_EDGE_NETWORKS'
    assert_contains frontend/nginx.conf 'Strict-Transport-Security $myexpenses_hsts always'
    assert_contains frontend/nginx.conf 'map $myexpenses_forwarded_proto $myexpenses_hsts'
    assert_contains frontend/nginx-mode.conf 'map $myexpenses_forwarded_proto $myexpenses_redirect_http'
    assert_contains frontend/Dockerfile 'ENTRYPOINT ["/entrypoint.sh"]'
    assert_contains frontend/entrypoint.sh 'MYEXPENSES_DEPLOYMENT_MODE'

    assert_contains nginx.single.conf 'location = /health/live'
    assert_contains nginx.single.conf 'location = /health/ready'
    assert_contains nginx.single.conf 'proxy_pass http://127.0.0.1:5000/health/ready;'
    assert_contains nginx.single.conf 'X-Forwarded-Proto $myexpenses_forwarded_proto'
    assert_contains nginx.single.conf 'myexpenses_trusted_edge:$http_x_forwarded_proto'
    assert_contains entrypoint.single.sh 'MYEXPENSES_TRUSTED_EDGE_NETWORKS'
    assert_contains nginx.single.conf 'Strict-Transport-Security $myexpenses_hsts always'
    assert_contains nginx.single.conf 'map $myexpenses_forwarded_proto $myexpenses_hsts'
    assert_contains entrypoint.single.sh 'configure_nginx_mode'

    assert_contains backend/Dockerfile 'HEALTHCHECK'
    assert_contains backend/Dockerfile "-w '%{http_code}'"
    assert_contains backend/Dockerfile 'X-Forwarded-Proto: https'
    assert_contains Dockerfile.single 'HEALTHCHECK'
    assert_contains Dockerfile.single "-w '%{http_code}'"
    assert_contains docker-compose.yml "-w '%{http_code}'"
    assert_contains docker-compose.yml 'X-Forwarded-Proto: https'
    assert_contains docker-compose.single.yml "-w '%{http_code}'"
    assert_contains frontend/Dockerfile 'X-Forwarded-Proto: https'
    assert_contains entrypoint.single.sh 'health/ready'
    assert_contains entrypoint.single.sh "-w '%{http_code}'"
    assert_contains scripts/smoke-deployment.sh "--write-out '%{http_code}'"
    assert_no_reusable_production_secrets

    assert_compose_requires_secrets docker-compose.yml
    assert_compose_requires_secrets docker-compose.single.yml
    assert_compose_allows_missing_bootstrap_secret docker-compose.yml
    assert_compose_allows_missing_bootstrap_secret docker-compose.single.yml
    assert_compose_is_valid docker-compose.yml
    assert_compose_is_valid docker-compose.single.yml
    assert_compose_overrides_are_preserved docker-compose.yml
    assert_compose_overrides_are_preserved docker-compose.single.yml

    printf 'deployment config tests passed\n'
}

main "$@"
