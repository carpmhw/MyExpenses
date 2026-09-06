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

# 驗證 backend-bearing image 使用可追蹤且一致的 OCI source/revision 契約。
assert_image_metadata_contract() {
    for file in backend/Dockerfile Dockerfile.single; do
        assert_contains "$file" 'ARG VCS_REF=unknown'
        assert_contains "$file" 'org.opencontainers.image.revision="${VCS_REF}"'
        assert_contains "$file" 'org.opencontainers.image.source="https://github.com/carpmhw/MyExpenses"'
    done

    for file in docker-compose.yml docker-compose.single.yml; do
        assert_contains "$file" 'VCS_REF: "${VCS_REF:-unknown}"'
    done

    checker=$ROOT_DIR/scripts/verify-image-metadata.sh
    [ -x "$checker" ] || fail 'scripts/verify-image-metadata.sh 不存在或不可執行'
    sh -n "$checker" || fail 'scripts/verify-image-metadata.sh shell syntax invalid'
    assert_contains scripts/verify-image-metadata.sh 'org.opencontainers.image.revision'
    assert_contains scripts/verify-image-metadata.sh 'org.opencontainers.image.source'
}

# 驗證正式 image metadata checker 會拒絕未知、錯誤及互相不一致的來源。
assert_image_metadata_rejects_invalid() (
    test_dir=$(mktemp -d)
    trap 'rm -rf "$test_dir"' EXIT
fake_docker="$test_dir/docker"
    cat >"$fake_docker" <<'EOF'
#!/bin/sh
if [ "${1:-}" != 'inspect' ]; then
    exit 1
fi
target=${4:-}
format=${3:-}
case "$format" in
    *'.Image'*)
        case "$target" in
            second) printf '%s\n' "${SECOND_IMAGE_ID:-}" ;;
            *) printf '%s\n' "${BACKEND_IMAGE_ID:-}" ;;
        esac
        ;;
    *'Config.Labels'*)
        case "$target" in
            second) printf '%s\n' "${SECOND_CONTAINER_METADATA:-}" ;;
            "$SECOND_IMAGE_ID") printf '%s\n' "${SECOND_IMAGE_METADATA:-}" ;;
            sha256:*) printf '%s\n' "${FAKE_IMAGE_METADATA:-}" ;;
            *) printf '%s\n' "${FAKE_CONTAINER_METADATA:-}" ;;
        esac
        ;;
    *)
        exit 1
        ;;
esac
EOF
    chmod +x "$fake_docker"

    revision=0123456789abcdef0123456789abcdef01234567
    image_id=sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
    second_image_id=sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
    source_url=https://github.com/carpmhw/MyExpenses

    if DOCKER_BIN="$fake_docker" BACKEND_IMAGE_ID="$image_id" FAKE_IMAGE_METADATA="unknown|$source_url" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" "$image_id" backend 2>/dev/null; then
        fail 'image metadata checker 接受 unknown revision'
    fi
    if DOCKER_BIN="$fake_docker" BACKEND_IMAGE_ID="$image_id" FAKE_IMAGE_METADATA="$revision|wrong-source" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" "$image_id" backend 2>/dev/null; then
        fail 'image metadata checker 接受錯誤 source'
    fi
    if DOCKER_BIN="$fake_docker" BACKEND_IMAGE_ID="$image_id" SECOND_IMAGE_ID="$second_image_id" \
        FAKE_IMAGE_METADATA="$revision|$source_url" SECOND_IMAGE_METADATA="$revision|$source_url" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" "$image_id" backend second 2>/dev/null; then
        fail 'image metadata checker 接受不一致 image ID'
    fi
    if DOCKER_BIN="$fake_docker" BACKEND_IMAGE_ID="$image_id" \
        FAKE_IMAGE_METADATA='|' FAKE_CONTAINER_METADATA="$revision|$source_url" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" "$image_id" backend 2>/dev/null; then
        fail 'image metadata checker 信任可偽造的 container labels'
    fi
    if DOCKER_BIN="$fake_docker" FAKE_METADATA="$image_id|$revision|$source_url" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" bad-image-id backend 2>/dev/null; then
        fail 'image metadata checker 接受無效 expected image ID'
    fi
    DOCKER_BIN="$fake_docker" BACKEND_IMAGE_ID="$image_id" SECOND_IMAGE_ID="$image_id" \
        FAKE_IMAGE_METADATA="$revision|$source_url" SECOND_IMAGE_METADATA="$revision|$source_url" \
        "$ROOT_DIR/scripts/verify-image-metadata.sh" "$revision" "$image_id" backend second || \
        fail 'image metadata checker 拒絕一致且有效的來源'
)

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

# 驗證 Development diagnostic Compose 也不接受缺少的 bootstrap secret。
assert_compose_requires_bootstrap_secret() {
    file=$1
    command -v docker >/dev/null 2>&1 || return 0
    docker compose version >/dev/null 2>&1 || return 0

    if MYEXPENSES_JWT_SECRET='test-jwt-secret-that-is-longer-than-32-characters' \
        env -u MYEXPENSES_BOOTSTRAP_SECRET \
        docker compose -p myexpenses-config-test -f "$ROOT_DIR/$file" config >/dev/null 2>&1; then
        fail "$file 在缺少 bootstrap secret 時仍可通過 compose config"
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

# 執行真實設定函數，驗證預設與自訂信任網路只輸出一次。
assert_nginx_networks_are_unique() (
    entrypoint=$1
    test_dir=$(mktemp -d)
    trap 'rm -rf "$test_dir"' EXIT
    awk -v output="$test_dir/mode.conf" '
        /^configure_nginx_mode\(\)/ { in_function = 1 }
        in_function {
            gsub("/etc/nginx/conf.d/00-myexpenses-mode.conf", output)
            print
        }
        in_function && /^}/ { exit }
    ' "$ROOT_DIR/$entrypoint" > "$test_dir/configure.sh"
    printf '\nconfigure_nginx_mode\n' >> "$test_dir/configure.sh"

    for networks in '' '127.0.0.1/32' '127.0.0.1/32,192.0.2.0/24 192.0.2.0/24,::1/128,::1/128'; do
        for mode in Local Lan Remote; do
            MYEXPENSES_DEPLOYMENT_MODE="$mode" MYEXPENSES_TRUSTED_EDGE_NETWORKS="$networks" \
                sh -eu "$test_dir/configure.sh" || fail "$entrypoint 設定產生失敗"
            [ "$(grep -Fc '    127.0.0.1/32 1;' "$test_dir/mode.conf")" -eq 1 ] || \
                fail "$entrypoint 重複輸出 loopback 信任網路"
            if [ "$networks" != '' ] && [ "$networks" != '127.0.0.1/32' ]; then
                [ "$(grep -Fc '    192.0.2.0/24 1;' "$test_dir/mode.conf")" -eq 1 ] || \
                    fail "$entrypoint 未保留唯一自訂 IPv4 網路"
                [ "$(grep -Fc '    ::1/128 1;' "$test_dir/mode.conf")" -eq 1 ] || \
                    fail "$entrypoint 未保留唯一自訂 IPv6 網路"
            fi
        done
    done
    if MYEXPENSES_TRUSTED_EDGE_NETWORKS='invalid;network' sh -eu "$test_dir/configure.sh" 2>/dev/null; then
        fail "$entrypoint 未拒絕無效信任網路"
    fi
)

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

    assert_contains docker-compose.development.yml 'ASPNETCORE_ENVIRONMENT: Development'
    assert_contains docker-compose.development.yml 'VCS_REF: "${VCS_REF:-unknown}"'
    assert_contains docker-compose.development.yml 'myexpenses-development-data:/app/data'
    assert_contains docker-compose.development.yml 'myexpenses-development-backups:/app/data/backups'
    assert_contains docker-compose.development.yml 'myexpenses-development-dataprotection:/app/keys'
    assert_contains docker-compose.development.yml 'MYEXPENSES_JWT_SECRET:?'
    assert_contains docker-compose.development.yml 'MYEXPENSES_BOOTSTRAP_SECRET:?'
    assert_not_contains docker-compose.development.yml 'ASPNETCORE_ENVIRONMENT=Production'
    assert_not_contains docker-compose.development.yml 'myexpenses-data:/app/data'
    assert_not_contains docker-compose.development.yml 'myexpenses-backups:/app/data/backups'
    assert_not_contains docker-compose.development.yml 'myexpenses-dataprotection:/app/keys'

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
    assert_image_metadata_contract
    assert_image_metadata_rejects_invalid
    assert_contains docker-compose.yml "-w '%{http_code}'"
    assert_contains docker-compose.yml 'X-Forwarded-Proto: https'
    assert_contains docker-compose.single.yml "-w '%{http_code}'"
    assert_contains frontend/Dockerfile 'X-Forwarded-Proto: https'
    assert_contains entrypoint.single.sh 'health/ready'
    assert_contains entrypoint.single.sh "-w '%{http_code}'"
    assert_contains scripts/smoke-deployment.sh "--write-out '%{http_code}'"
    assert_no_reusable_production_secrets

    assert_nginx_networks_are_unique frontend/entrypoint.sh
    assert_nginx_networks_are_unique entrypoint.single.sh

    assert_compose_requires_secrets docker-compose.yml
    assert_compose_requires_secrets docker-compose.single.yml
    assert_compose_requires_secrets docker-compose.development.yml
    assert_compose_requires_bootstrap_secret docker-compose.development.yml
    assert_compose_allows_missing_bootstrap_secret docker-compose.yml
    assert_compose_allows_missing_bootstrap_secret docker-compose.single.yml
    assert_compose_is_valid docker-compose.yml
    assert_compose_is_valid docker-compose.single.yml
    assert_compose_is_valid docker-compose.development.yml
    assert_compose_overrides_are_preserved docker-compose.yml
    assert_compose_overrides_are_preserved docker-compose.single.yml

    printf 'deployment config tests passed\n'
}

main "$@"
