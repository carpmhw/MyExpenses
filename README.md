# MyExpenses

個人記帳系統，追蹤提款、支出、信用卡分期、銀行帳戶、股票與財務快照。支援 MCP 協議，可透過 AI agent 直接記帳。

## 技術棧

- **Backend**: .NET 10, SQLite, Entity Framework Core, JWT 認證
- **Frontend**: Vue 3, TypeScript, Vite, Tailwind CSS v4, Chart.js
- **MCP Server**: Node.js, @modelcontextprotocol/sdk
- **部署**: Docker Compose

## 快速開始

```bash
git clone <repo-url> MyExpenses
cd MyExpenses
docker compose up -d
```

前端：`http://localhost`
後端 API：`http://localhost:5000`

開發模式（需要 .NET 10 SDK 和 Node.js）：

```bash
# Backend
cd backend/MyExpenses.Api
dotnet run

# Frontend
cd frontend
npm install
npm run dev
```

## 環境變數

### Backend

| 變數 | 說明 | 預設值 |
|------|------|--------|
| `ASPNETCORE_ENVIRONMENT` | 環境模式 | `Production` |
| `ConnectionStrings__DefaultConnection` | SQLite 路徑 | `Data Source=/app/data/MyExpenses.db` |
| `Jwt__Secret` | JWT 簽章金鑰（至少 32 字元） | 內建預設值（僅開發用） |
| `Jwt__Issuer` | JWT 發行者 | `MyExpenses` |
| `Jwt__Audience` | JWT 受眾 | `MyExpenses` |
| `Jwt__ExpiryMinutes` | Token 有效時間（分鐘） | `1440` |
| `TimeZone__Default` | 預設時區 | `Asia/Taipei` |
| `TZ` | Docker 容器時區 | `Asia/Taipei` |

### MCP Server

| 變數 | 說明 | 必填 |
|------|------|------|
| `MYEXPENSES_API_URL` | 後端 API 位址 | 否（預設 `http://localhost:5000`） |
| `MYEXPENSES_API_TOKEN` | API Token（格式 `oc_xxx`） | 是 |

## MCP 整合

MCP server 提供以下工具，可透過任何 MCP client（如 Claude Desktop、Cursor 等）呼叫：

| 工具 | 說明 |
|------|------|
| `create_transaction` | 記帳（收入 / 支出） |
| `list_categories` | 查詢收支分類 |
| `list_payment_methods` | 查詢支付方式 |
| `get_recent_transactions` | 查詢最近交易紀錄 |
| `get_financial_summary` | 取得本月收支摘要 |
| `undo_transaction` | 復原已刪除的交易 |

設定範例（OpenCode）：

```json
{
  "mcpServers": {
    "myexpenses": {
      "command": "node",
      "args": ["/path/to/backend/myexpenses-mcp-server/dist/index.js"],
      "env": {
        "MYEXPENSES_API_URL": "http://localhost:5000",
        "MYEXPENSES_API_TOKEN": "oc_你的金鑰"
      }
    }
  }
}
```

## License

MIT
