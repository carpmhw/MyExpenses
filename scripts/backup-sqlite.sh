#!/bin/sh
set -eu

# 這是停止或線上 SQLite backup 的 operator wrapper；它不會修改 active database。
umask 077

# 顯示 backup wrapper 的參數格式並以 usage 狀態結束。
usage() {
    printf '%s\n' "用法: $0 <active-database> <backup-directory> <migration-identity> [retention-limit]" >&2
    exit 2
}

# 回報不含 database 內容的 operator 錯誤並結束。
fail() {
    printf 'backup failed: %s\n' "$1" >&2
    exit 1
}

# 確認 SQLite metadata 字串可安全寫入固定 SQL template。
validate_metadata_value() {
    value=$1
    case "$value" in
        ''|*[!A-Za-z0-9_.-]*) fail "migration identity 只能包含英數字、點、底線或連字號。" ;;
    esac
}

# 確認路徑不包含會破壞 sqlite3 dot-command quoting 的單引號。
validate_path() {
    value=$1
    case "$value" in
        *"'"*) fail "database path 不可包含單引號。" ;;
    esac
}

# 執行 SQLite integrity check 並要求所有結果都是 ok。
check_integrity() {
    integrity_result=$(sqlite3 -batch -noheader "$1" 'PRAGMA integrity_check;') || \
        fail "$2"
    [ "$integrity_result" = "ok" ] || fail "$2"
}

# 只清理已寫入 verified metadata 的舊 backup，避免失敗操作刪除 recovery point。
retain_verified_backups() {
    directory=$1
    retention=$2
    verified_list=$(mktemp "$directory/.myexpenses-verified.XXXXXX") || \
        fail "無法建立 retention 暫存檔。"

# 清理 retention 暫存清單，不修改任何 verified backup。
    cleanup_verified_list() {
        rm -f -- "$verified_list"
    }
    trap cleanup_verified_list EXIT INT TERM

    for backup_path in "$directory"/myexpenses-backup-*.db; do
        [ -f "$backup_path" ] || continue
        verified_at=$(sqlite3 -batch -noheader "$backup_path" \
            "SELECT VerifiedAtUtc FROM __MyExpensesBackupMetadata WHERE Id = 1 AND IntegrityCheck = 'ok';" \
            2>/dev/null || true)
        [ -n "$verified_at" ] || continue
        printf '%s|%s\n' "$verified_at" "$backup_path" >> "$verified_list"
    done

    count=0
    sort -r "$verified_list" | while IFS='|' read -r verified_at backup_path; do
        [ -n "$backup_path" ] || continue
        count=$((count + 1))
        if [ "$count" -gt "$retention" ]; then
            rm -f -- "$backup_path"
        fi
    done

    trap - EXIT INT TERM
    cleanup_verified_list
}

[ "$#" -ge 3 ] && [ "$#" -le 4 ] || usage

active_database=$1
backup_directory=$2
migration_identity=$3
retention_limit=${4:-7}

command -v sqlite3 >/dev/null 2>&1 || fail "需要 sqlite3 指令。"
validate_path "$active_database"
validate_path "$backup_directory"
validate_metadata_value "$migration_identity"
case "$retention_limit" in
    ''|*[!0-9]*|0) fail "retention limit 必須是大於零的整數。" ;;
esac
[ -f "$active_database" ] || fail "active database 不存在。"

latest_migration=$(sqlite3 -batch -noheader "$active_database" \
    'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;' \
    2>/dev/null) || fail "無法讀取 active database migration history。"
[ "$latest_migration" = "$migration_identity" ] || \
    fail "提供的 migration identity 與 active database 不一致。"

mkdir -p -- "$backup_directory"
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
created_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
final_path="$backup_directory/myexpenses-backup-$timestamp-$$.db"
temporary_path=$(mktemp "$backup_directory/.myexpenses-backup.XXXXXX") || \
    fail "無法建立 temporary backup output。"

# 清理失敗操作留下的 temporary backup，保留 active database 與已發布 recovery point。
cleanup() {
    status=$?
    [ -z "${temporary_path:-}" ] || rm -f -- "$temporary_path"
    exit "$status"
}
trap cleanup EXIT INT TERM

# 使用 SQLite backup primitive，避免只複製 live main file 而遺漏 WAL committed data。
sqlite3 -batch "$active_database" ".backup '$temporary_path'" || \
    fail "SQLite consistent backup 失敗。"
chmod 600 -- "$temporary_path"

schema_version=$(sqlite3 -batch -noheader "$temporary_path" 'PRAGMA schema_version;') || \
    fail "無法讀取 backup schema version。"
case "$schema_version" in
    ''|*[!0-9]*) fail "backup schema version 無效。" ;;
esac

sqlite3 -batch "$temporary_path" <<EOF || fail "無法寫入 backup metadata。"
CREATE TABLE IF NOT EXISTS __MyExpensesBackupMetadata (
    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    CreatedAtUtc TEXT NOT NULL,
    VerifiedAtUtc TEXT NOT NULL DEFAULT '',
    MigrationIdentity TEXT NOT NULL,
    SourceSchemaVersion INTEGER NOT NULL,
    IntegrityCheck TEXT NOT NULL
);
DELETE FROM __MyExpensesBackupMetadata;
INSERT INTO __MyExpensesBackupMetadata
    (Id, CreatedAtUtc, VerifiedAtUtc, MigrationIdentity, SourceSchemaVersion, IntegrityCheck)
VALUES (1, '$created_at', '', '$migration_identity', $schema_version, 'pending');
EOF

check_integrity "$temporary_path" "backup integrity check 未通過。"
verified_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
sqlite3 -batch "$temporary_path" \
    "UPDATE __MyExpensesBackupMetadata SET VerifiedAtUtc = '$verified_at', IntegrityCheck = 'ok' WHERE Id = 1;" || \
    fail "無法標記 verified backup。"
check_integrity "$temporary_path" "metadata 寫入後的 backup integrity check 未通過。"

mv -- "$temporary_path" "$final_path"
temporary_path=''
retain_verified_backups "$backup_directory" "$retention_limit"

printf 'backup succeeded: %s\n' "$final_path"
