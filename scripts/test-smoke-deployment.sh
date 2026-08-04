#!/bin/sh
set -eu

ROOT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
SCRIPT="$ROOT_DIR/scripts/smoke-deployment.sh"

# 顯示測試失敗原因並以失敗狀態結束。
fail() {
    printf 'deployment smoke contract test failed: %s\n' "$1" >&2
    exit 1
}

# 驗證 smoke script 包含必要的部署契約片段。
assert_contains() {
    expected=$1
    grep -Fq -- "$expected" "$SCRIPT" || fail "smoke script 缺少設定片段: $expected"
}

# 驗證 smoke script 可以被 POSIX shell 解析。
assert_shell_syntax() {
    sh -n "$SCRIPT" || fail 'smoke script shell syntax invalid'
}

# 驗證 smoke script 的 usage 暴露三種部署模式與安全覆寫選項。
assert_usage() {
    usage=$($SCRIPT --help) || fail 'smoke script --help failed'
    printf '%s\n' "$usage" | grep -Fq -- 'local' || fail 'usage 缺少 local mode'
    printf '%s\n' "$usage" | grep -Fq -- 'lan' || fail 'usage 缺少 lan mode'
    printf '%s\n' "$usage" | grep -Fq -- 'remote' || fail 'usage 缺少 remote mode'
    printf '%s\n' "$usage" | grep -Fq -- 'SMOKE_SKIP_BUILD' || fail 'usage 缺少 build override'
    printf '%s\n' "$usage" | grep -Fq -- 'COMPOSE_PROJECT_NAME' || fail 'usage 缺少 project override'
}

# 驗證 live Remote path 也會建立 owner setup payload，而不是讀取不存在的暫存檔。
assert_occurrences_at_least() {
    expected=$1
    minimum=$2
    occurrences=$(grep -Fc -- "$expected" "$SCRIPT")
    [ "$occurrences" -ge "$minimum" ] || fail "smoke script 的 $expected 使用次數不足"
}

# 執行 smoke script 的靜態契約檢查。
main() {
    [ -f "$SCRIPT" ] || fail 'scripts/smoke-deployment.sh 不存在'
    assert_shell_syntax
    assert_contains 'MYEXPENSES_JWT_SECRET'
    assert_contains 'MYEXPENSES_BOOTSTRAP_SECRET'
    assert_contains '/health/live'
    assert_contains '/health/ready'
    assert_contains '/api/auth/status'
    assert_contains 'docker compose'
    assert_contains '--volumes'
    assert_contains '5000'
    assert_contains 'X-Forwarded-Proto'
    assert_contains 'Strict-Transport-Security'
    assert_contains 'Content-Security-Policy'
    assert_contains 'Deployment__TrustedNetworks__0'
    assert_contains 'trusted home network'
    assert_contains 'healthy_attempt=$((healthy_attempt + 1))'
    assert_contains 'NetworkSettings.Ports'
    assert_contains 'extract_bearer_token'
    assert_contains 'Authorization: Bearer'
    assert_contains 'umask 077'
    assert_occurrences_at_least 'prepare_owner_payloads' 3
    assert_usage
    printf 'deployment smoke contract tests passed\n'
}

main "$@"
