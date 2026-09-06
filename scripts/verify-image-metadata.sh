#!/bin/sh
set -eu

DOCKER_BIN=${DOCKER_BIN:-docker}
SOURCE_URL='https://github.com/carpmhw/MyExpenses'

# 顯示 image metadata checker 的用法。
usage() {
    cat <<'EOF'
Usage: scripts/verify-image-metadata.sh <expected-revision> <expected-image-id> <container> [<container> ...]

The expected revision must be a non-unknown hexadecimal VCS revision. Every
listed container must use the expected content-addressed image ID and expose
the same OCI revision and repository source.
EOF
}

# 顯示不含敏感內容的失敗原因並以失敗狀態結束。
fail() {
    printf 'image metadata verification failed: %s\n' "$1" >&2
    exit 1
}

# 驗證正式驗收使用的來源 revision 不是空值、unknown 或無效字串。
validate_expected_revision() {
    expected_revision=$1
    case "$expected_revision" in
        ''|unknown|UNKNOWN)
            fail 'expected revision must not be empty or unknown'
            ;;
        *[![:xdigit:]]*)
            fail 'expected revision must be hexadecimal'
            ;;
    esac
    if [ "${#expected_revision}" -lt 7 ]; then
        fail 'expected revision is too short'
    fi
}

# 驗證 image ID 是完整的 SHA-256 content address，而不是 tag 或任意文字。
validate_image_id() {
    image_id=$1
    case "$image_id" in
        sha256:*) ;;
        *) fail 'image ID must be a sha256 content address' ;;
    esac

    image_digest=${image_id#sha256:}
    case "$image_digest" in
        ''|*[![:xdigit:]]*) fail 'image ID digest must be hexadecimal' ;;
    esac
    [ "${#image_digest}" -eq 64 ] || fail 'image ID digest must contain 64 hexadecimal characters'
}

# 從指定 container 讀取實際 image ID，再從該 image config 讀取 OCI labels。
read_container_metadata() {
    container=$1
    container_image_id=$($DOCKER_BIN inspect --format '{{.Image}}' "$container" 2>/dev/null) || \
        fail "cannot inspect container image ID: $container"
    [ -n "$container_image_id" ] || fail "container image ID is empty: $container"

    metadata=$($DOCKER_BIN inspect --format \
        '{{ index .Config.Labels "org.opencontainers.image.revision" }}|{{ index .Config.Labels "org.opencontainers.image.source" }}' \
        "$container_image_id" 2>/dev/null) || fail "cannot inspect image metadata: $container_image_id"
    case "$metadata" in
        *'|'*) ;;
        *) fail "container metadata is incomplete: $1" ;;
    esac
    container_revision=${metadata%%|*}
    container_source=${metadata#*|}
}

# 驗證單一 container 的實際 image ID、revision/source 與正式期待值完全一致。
verify_container_metadata() {
    container=$1
    read_container_metadata "$container"

    validate_image_id "$container_image_id"
    [ "$container_image_id" = "$expected_image_id" ] || \
        fail "container image ID does not match expected image: $container"
    case "$container_revision" in
        ''|unknown|UNKNOWN)
            fail "container has an unknown revision: $container"
            ;;
        *[![:xdigit:]]*)
            fail "container revision is not hexadecimal: $container"
            ;;
    esac
    [ "$container_revision" = "$expected_revision" ] || \
        fail "container revision does not match expected source: $container"
    [ "$container_source" = "$SOURCE_URL" ] || \
        fail "container source does not match the repository: $container"
}

# 驗證所有指定 backend-bearing containers 使用同一個已確認來源。
main() {
    if [ "$#" -eq 1 ] && [ "$1" = '--help' ]; then
        usage
        exit 0
    fi
    [ "$#" -ge 3 ] || {
        usage >&2
        exit 2
    }

    expected_revision=$1
    expected_image_id=$2
    shift 2
    validate_expected_revision "$expected_revision"
    validate_image_id "$expected_image_id"
    for container in "$@"; do
        [ -n "$container" ] || fail 'container identifier must not be empty'
        verify_container_metadata "$container"
    done
    printf 'image metadata verification passed: %s\n' "$expected_image_id"
}

main "$@"
