# 映像來源與 Query 10103 診斷

## 什麼時候需要看這份文件？

平常安裝與使用 MyExpenses，照 [README](../README.md#快速開始) 操作即可。這份文件是給維護時排查問題用的，主要處理兩件事：

1. 確認 Docker 容器跑的確實是預期版本，而不是舊映像。
2. 日誌出現 `Query[10103]` 時，查找相關訊息，必要時用獨立測試環境重現。

`Query[10103]` 是 EF Core（後端存取資料庫的工具）的警告代碼，不是帳目編號。它表示程式在沒有排序、也沒有篩選條件的情況下取「第一筆」資料，結果可能不固定。詳見 [Microsoft 官方說明](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.diagnostics.coreeventid.firstwithoutorderbyandfilterwarning?view=efcore-10.0)。

本文命令皆從專案根目錄執行。Bash 範例請在獨立的 Bash 工作階段中操作；其中的 `set -eu` 會在命令失敗或變數未設定時結束 shell，`trap cleanup` 則會在離開時清理測試資料。不要直接在仍有其他工作的 shell 中執行。

## 1. 確認跑的是哪個版本

Docker 映像可以想成打包好的程式，容器則是正在執行的程式。映像名稱後面的 tag（例如 `latest`）可以指向不同內容，所以光看名稱不足以確認版本。

本專案的兩份後端 Dockerfile（`backend/Dockerfile`、`Dockerfile.single`）會在映像中附上來源資訊，稱為 OCI labels：

| 欄位 | 白話說明 |
| --- | --- |
| `org.opencontainers.image.revision` | 這份映像是由哪一次 Git commit 建立的。 |
| `org.opencontainers.image.source` | 原始碼來自哪個 Git repository。 |
| Image ID | 用來核對容器實際使用的映像內容，而不只是名稱。這不是 OCI label。 |

建置時透過 `VCS_REF` 傳入 Git commit 編號。沒有設定時會記成 `unknown`，表示無法從這個欄位確認版本；本機測試可以接受，但正式版本驗收不可以。

### 執行前先確認

- 已依 README 設定 `MYEXPENSES_JWT_SECRET` 與 `MYEXPENSES_BOOTSTRAP_SECRET`。
- 工作目錄沒有未提交或未追蹤的檔案，避免映像內容與標示的 commit 不一致。下方命令遇到這種情況會停止，請先檢查變更，不要為了通過檢查而直接刪除檔案。
- `18081` 與 `18082` 連接埠未被占用；若已使用，請修改下方兩個 port 變數。

下方會分別建立「雙容器版」與「單一映像版」的臨時測試環境，重新建置並核對版本。`-p` 指定本次專用的 Compose 專案名稱，`$$` 會帶入目前 shell 的程序編號，降低與其他測試撞名的機會。

> [!WARNING]
> 離開這個 shell 時，清理函數會刪除本次測試容器與資料卷（volumes，也就是容器保存資料的空間）。請保留專用專案名稱，不要改成正式環境的名稱，也不要掛載正式資料卷。

```bash
set -eu
test -z "$(git status --porcelain=v1 --untracked-files=all)" || {
  printf 'source must be a clean committed revision\n' >&2
  exit 1
}
export VCS_REF="$(git rev-parse HEAD)"
default_project="myexpenses-query10103-default-$$"
single_project="myexpenses-query10103-single-$$"
default_port=18081
single_port=18082

# 輸出日誌並僅清理本次建立的兩個驗收 project 與 volumes。
cleanup() {
  docker compose -p "$default_project" logs --no-color || true
  docker compose -p "$single_project" -f docker-compose.single.yml logs --no-color || true
  docker compose -p "$default_project" down --volumes --remove-orphans || true
  docker compose -p "$single_project" -f docker-compose.single.yml down --volumes --remove-orphans || true
}
trap cleanup EXIT INT TERM

export MYEXPENSES_HTTP_PORT="$default_port"
docker compose -p "$default_project" build --no-cache
docker compose -p "$default_project" up -d --wait
backend_container="$(docker compose -p "$default_project" ps -q backend)"
backend_image_id="$(docker compose -p "$default_project" images -q backend)"
test -n "$backend_image_id"
sh scripts/verify-image-metadata.sh "$VCS_REF" "$backend_image_id" "$backend_container"
docker inspect --format '{{.Id}} {{.Image}}' "$backend_container"

export MYEXPENSES_HTTP_PORT="$single_port"
docker compose -p "$single_project" -f docker-compose.single.yml build --no-cache
docker compose -p "$single_project" -f docker-compose.single.yml up -d --wait
app_container="$(docker compose -p "$single_project" -f docker-compose.single.yml ps -q app)"
app_image_id="$(docker compose -p "$single_project" -f docker-compose.single.yml images -q app)"
test -n "$app_image_id"
sh scripts/verify-image-metadata.sh "$VCS_REF" "$app_image_id" "$app_container"
docker inspect --format '{{.Id}} {{.Image}}' "$app_container"
```

### 怎麼看結果？

兩次檢查都應顯示 `image metadata verification passed: sha256:...`，代表這兩個測試容器使用的映像 ID、Git commit 與來源 repository 都符合預期。這只確認映像來源，不代表所有功能都已測試通過，也不會驗證另一個已部署的正式容器。

出現 `image metadata verification failed` 時，請看後面的原因：可能是版本資訊缺漏、值為 `unknown`、來源 repository 不對，或容器使用的映像與預期不同。應確認建置來源與容器使用的映像，不要只改 tag 名稱就當作修好了。

## 2. 在日誌中找 Query 10103

完成上一步後，先不要離開 shell，因為離開就會清掉測試容器。在同一個 shell 執行以下命令，只列出符合警告代碼或名稱的日誌：

```bash
docker compose -p "$default_project" logs backend 2>&1 | grep -E 'Microsoft\.EntityFrameworkCore\.Query\[10103\]|FirstWithoutOrderByAndFilterWarning' || true
docker compose -p "$single_project" -f docker-compose.single.yml logs app 2>&1 | grep -E 'Microsoft\.EntityFrameworkCore\.Query\[10103\]|FirstWithoutOrderByAndFilterWarning' || true
```

沒有輸出，表示這次讀到的日誌沒有符合的訊息，不代表所有操作都不會觸發警告。如果 Docker 本身報錯，請先確認容器還在執行、專案名稱也正確，再查看未篩選的完整日誌。

Windows PowerShell 可以使用以下查詢命令。這只是查日誌，不會建立測試環境；請將專案名稱換成實際仍在執行的名稱：

```powershell
$defaultProject = 'myexpenses-query10103-default-12345'
$singleProject = 'myexpenses-query10103-single-12345'
docker compose -p $defaultProject logs backend 2>&1 | Select-String 'Microsoft\.EntityFrameworkCore\.Query\[10103\]|FirstWithoutOrderByAndFilterWarning'
docker compose -p $singleProject -f docker-compose.single.yml logs app 2>&1 | Select-String 'Microsoft\.EntityFrameworkCore\.Query\[10103\]|FirstWithoutOrderByAndFilterWarning'
```

找到警告後，請保留前後日誌、當時做了什麼操作，以及映像版本資訊，方便追查是哪一段查詢造成的。

## 3. 用獨立環境重現問題

如果需要進一步排查，可用 `docker-compose.development.yml` 啟動開發模式的測試環境，不使用正式資料。請另開一個 Bash 工作階段執行，避免覆蓋前面驗收環境的清理函數。

下方命令會產生臨時密鑰、使用獨立專案名稱，並透過 `18080` 連接埠提供服務。若連接埠已被占用，請修改 `diagnostic_port`。

```bash
set -eu
diagnostic_project="myexpenses-query10103-$$"
diagnostic_port=18080
export MYEXPENSES_DIAGNOSTIC_HTTP_PORT="$diagnostic_port"
export MYEXPENSES_JWT_SECRET="$(openssl rand -hex 32)"
export MYEXPENSES_BOOTSTRAP_SECRET="$(openssl rand -hex 32)"

# 僅清理本次建立的隔離診斷 project 與 volumes。
cleanup() {
  docker compose -p "$diagnostic_project" -f docker-compose.development.yml logs --no-color || true
  docker compose -p "$diagnostic_project" -f docker-compose.development.yml down --volumes --remove-orphans || true
}
trap cleanup EXIT INT TERM

docker compose -p "$diagnostic_project" -f docker-compose.development.yml build --no-cache
docker compose -p "$diagnostic_project" -f docker-compose.development.yml up -d --wait
curl --fail http://127.0.0.1:"$diagnostic_port"/health/live
curl --fail http://127.0.0.1:"$diagnostic_port"/health/ready
docker compose -p "$diagnostic_project" -f docker-compose.development.yml logs --no-color backend
```

兩個 `curl --fail` 命令都成功，表示服務通過存活與就緒檢查。接著可開啟 `http://127.0.0.1:18080`（若有修改連接埠，請一併調整網址），依 README 的首次設定流程建立測試 owner，再嘗試重現問題並查看後端日誌。這是新的測試資料庫，不會自動包含正式環境的帳目。

本機排查時可以保留 `VCS_REF=unknown`；若要確認正式版本來源，仍需執行第 1 節的驗收流程。

> [!WARNING]
> 此環境只供臨時測試。離開 shell 時會輸出日誌，並刪除本次專案的容器與資料卷。需要的診斷資訊請先保留，分享日誌前請檢查是否包含敏感資料。不要掛載正式資料庫、備份或 Data Protection keys（用來保護登入工作階段的金鑰），也不要把清理命令的專案名稱換成正式環境。
