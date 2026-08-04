# Vue 3 + TypeScript + Vite

This template should help get you started developing with Vue 3 and TypeScript in Vite. The template uses Vue 3 `<script setup>` SFCs, check out the [script setup docs](https://v3.vuejs.org/api/sfc-script-setup.html#sfc-script-setup) to learn more.

Learn more about the recommended Project Setup and IDE Support in the [Vue Docs TypeScript Guide](https://vuejs.org/guide/typescript/overview.html#project-setup).

## 非同步資料可靠性

核心頁面使用 `useAsyncQuery` 管理以 `normalizeQueryKey` 產生的查詢身份。查詢函式必須接收 `AbortSignal` 並傳給 `api`，查詢身份改變時會清除舊資料，同身份 `refresh()` 則保留舊資料並呈現 refreshing/stale 狀態。

使用 `useAsyncMutation` 執行需要伺服器確認的寫入。按鈕流程應等待 `submit()` 完成，再使用 canonical response 或呼叫受影響 query 的 `refresh()`；refresh 失敗不可把已確認的 mutation 改報為失敗。金融 create command 的 retry key 由 `createIdempotencyKeyState` 依 canonical payload 管理，未變更的 uncertain retry 會重用原 key。

查詢內容應以 `QueryState` 或帶有 `error`、`refreshing`、`retry` 的 `DataTable` 呈現。取消或被較新 query 取代的 request 不應顯示錯誤；真正的 error、empty、stale 與 last-success 時間則必須保持可見。
