#!/bin/sh
set -eu

# 這是停止應用程式後使用的 SQLite restore wrapper；它不會啟動或修改應用程式設定。
umask 077

# 顯示 restore wrapper 的參數格式並以 usage 狀態結束。
usage() {
    printf '%s\n' "用法: $0 <active-database> <verified-backup>" >&2
    exit 2
}

# 回報不含 database 內容的 operator 錯誤並結束。
fail() {
    printf 'restore failed: %s\n' "$1" >&2
    exit 1
}

# 執行 SQLite integrity check 並拒絕非 ok 的結果。
check_integrity() {
    integrity_result=$(sqlite3 -batch -noheader "$1" 'PRAGMA integrity_check;') || \
        fail "$2"
    [ "$integrity_result" = "ok" ] || fail "$2"
}

[ "$#" -eq 2 ] || usage

active_database=$1
selected_backup=$2

command -v sqlite3 >/dev/null 2>&1 || fail "需要 sqlite3 指令。"
[ -f "$selected_backup" ] || fail "選定的 backup 不存在。"

active_directory=$(dirname -- "$active_database")
mkdir -p -- "$active_directory"
active_database=$(CDPATH= cd -- "$active_directory" && pwd)/$(basename -- "$active_database")
selected_backup=$(CDPATH= cd -- "$(dirname -- "$selected_backup")" && pwd)/$(basename -- "$selected_backup")
[ "$active_database" != "$selected_backup" ] || fail "backup 路徑必須與 active database 不同。"

active_directory=$(dirname -- "$active_database")

backup_integrity=$(sqlite3 -batch -noheader "$selected_backup" 'PRAGMA integrity_check;') || fail "無法讀取 backup。"
[ "$backup_integrity" = "ok" ] || fail "backup integrity check 未通過。"

metadata_integrity=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT IntegrityCheck FROM __MyExpensesBackupMetadata WHERE Id = 1;") || fail "無法讀取 backup metadata。"
[ "$metadata_integrity" = "ok" ] || fail "backup metadata 尚未驗證。"

created_at=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT CreatedAtUtc FROM __MyExpensesBackupMetadata WHERE Id = 1;") || fail "backup metadata 不完整。"
verified_at=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT VerifiedAtUtc FROM __MyExpensesBackupMetadata WHERE Id = 1;") || fail "backup metadata 不完整。"
migration_identity=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT MigrationIdentity FROM __MyExpensesBackupMetadata WHERE Id = 1;") || fail "backup metadata 不完整。"
source_schema_version=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT SourceSchemaVersion FROM __MyExpensesBackupMetadata WHERE Id = 1;") || fail "backup metadata 不完整。"
[ -n "$created_at" ] && [ -n "$verified_at" ] && [ -n "$migration_identity" ] || \
    fail "backup metadata 欄位不完整。"

case "$source_schema_version" in
    ''|*[!0-9]*) fail "backup metadata schema version 無效。" ;;
esac

schema_version=$(sqlite3 -batch -noheader "$selected_backup" 'PRAGMA schema_version;') || \
    fail "無法讀取 backup schema。"
case "$schema_version" in
    ''|0|*[!0-9]*) fail "backup schema 無效。" ;;
esac

history_table_count=$(sqlite3 -batch -noheader "$selected_backup" \
    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';") || \
    fail "無法讀取 EF migration history。"
[ "$history_table_count" = "1" ] || fail "backup 缺少 EF migration history。"

latest_migration=$(sqlite3 -batch -noheader "$selected_backup" \
    'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;') || \
    fail "無法讀取最新 migration identity。"
[ "$latest_migration" = "$migration_identity" ] || \
    fail "backup metadata 與 EF migration history 不一致。"

temporary_output=$(mktemp "$active_directory/.myexpenses-restore.XXXXXX") || \
    fail "無法建立 temporary restore output。"
rollback_path=''
rollback_temporary=''
staged_wal=''
staged_shm=''
active_replaced=0

# atomic replacement 失敗時將 WAL 與 shared-memory sidecar 放回 active path。
restore_staged_sidecars() {
    if [ "$active_replaced" -eq 0 ]; then
        if [ -n "$staged_shm" ] && [ -e "$staged_shm" ] && [ ! -e "$active_database-shm" ]; then
            mv -- "$staged_shm" "$active_database-shm" || true
        fi
        if [ -n "$staged_wal" ] && [ -e "$staged_wal" ] && [ ! -e "$active_database-wal" ]; then
            mv -- "$staged_wal" "$active_database-wal" || true
        fi
    fi
}

# 清理 temporary restore files，並在需要時復原 sidecar。
cleanup() {
    status=$?
    restore_staged_sidecars
    [ -z "$temporary_output" ] || rm -f -- "$temporary_output"
    [ -z "$rollback_temporary" ] || rm -f -- "$rollback_temporary"
    exit "$status"
}

trap cleanup EXIT INT TERM

cp -- "$selected_backup" "$temporary_output" || fail "無法建立 temporary restore output。"
chmod 600 -- "$temporary_output"
check_integrity "$temporary_output" "temporary restore output integrity check 未通過。"

if [ -f "$active_database" ]; then
    rollback_path="$active_database.rollback-$(date -u +%Y%m%dT%H%M%SZ)-$$.db"
    rollback_temporary="$rollback_path.tmp"
    sqlite3 -batch "$active_database" ".backup '$rollback_temporary'" || \
        fail "無法建立 current database rollback copy。"
    chmod 600 -- "$rollback_temporary"
    check_integrity "$rollback_temporary" "current database rollback copy integrity check 未通過。"
    mv -- "$rollback_temporary" "$rollback_path"
    rollback_temporary=''

    if [ -e "$active_database-wal" ]; then
        staged_wal="$rollback_path-wal"
        mv -- "$active_database-wal" "$staged_wal" || fail "無法暫存 active WAL sidecar。"
    fi
    if [ -e "$active_database-shm" ]; then
        staged_shm="$rollback_path-shm"
        mv -- "$active_database-shm" "$staged_shm" || fail "無法暫存 active shared-memory sidecar。"
    fi
fi

mv -f -- "$temporary_output" "$active_database" || fail "無法 atomic replace active database。"
temporary_output=''
active_replaced=1

printf 'restore succeeded: migration=%s rollback=%s\n' "$migration_identity" "${rollback_path:-none}"
