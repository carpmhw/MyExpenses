#!/usr/bin/env node
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';

const API_URL = process.env.MYEXPENSES_API_URL || 'http://localhost:5000';
const API_TOKEN = process.env.MYEXPENSES_API_TOKEN;
if (!API_TOKEN) {
  console.error('MYEXPENSES_API_TOKEN environment variable is required');
  process.exit(1);
}

async function apiCall(path: string, options?: { method?: string; body?: unknown }) {
  const res = await fetch(`${API_URL}${path}`, {
    method: options?.method || 'GET',
    headers: {
      'Authorization': `Bearer ${API_TOKEN}`,
      'Content-Type': 'application/json',
    },
    body: options?.body ? JSON.stringify(options.body) : undefined,
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API error ${res.status}: ${text}`);
  }
  return res.json();
}

const server = new Server(
  { name: 'myexpenses-mcp-server', version: '1.0.0' },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: 'create_transaction',
      description: '記帳：新增一筆收入或支出。可接受類別名稱（如「飲食」）或代碼（如「food」），支付方式可傳名稱（如「現金」、「信用卡」）或代碼（如「cash」）',
      inputSchema: {
        type: 'object',
        properties: {
          amount: { type: 'number', description: '金額' },
          description: { type: 'string', description: '交易描述，如「午餐」' },
          category: { type: 'string', description: '類別名稱或 systemCode，如「飲食」或「food」' },
          date: { type: 'string', description: '日期 YYYY-MM-DD，預設今天' },
          paymentMethod: { type: 'string', description: '支付方式' },
          notes: { type: 'string', description: '備註' },
        },
        required: ['amount', 'description'],
      },
    },
    {
      name: 'list_categories',
      description: '取得所有收支分類',
      inputSchema: {
        type: 'object',
        properties: {
          type: { type: 'string', description: '篩選：income 或 expense' },
        },
      },
    },
    {
      name: 'get_recent_transactions',
      description: '查詢最近的交易紀錄',
      inputSchema: {
        type: 'object',
        properties: {
          limit: { type: 'number', description: '筆數', default: 5 },
        },
      },
    },
    {
      name: 'undo_transaction',
      description: '復原一筆被刪除的交易',
      inputSchema: {
        type: 'object',
        properties: {
          id: { type: 'number', description: '交易 ID' },
        },
        required: ['id'],
      },
    },
    {
      name: 'list_payment_methods',
      description: '取得所有支付方式',
      inputSchema: { type: 'object', properties: {} },
    },
    {
      name: 'get_financial_summary',
      description: '取得本月收支摘要',
      inputSchema: { type: 'object', properties: {} },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  switch (name) {
    case 'create_transaction': {
      const { amount, description, category, date, paymentMethod, notes } = args as any;
      const body: any = { amount, description };
      if (category) body.category = category;
      if (date) body.date = date;
      if (paymentMethod) body.paymentMethod = paymentMethod;
      if (notes) body.notes = notes;
      const result = await apiCall('/api/transactions', { method: 'POST', body });
      const categoryName = result.category?.name || category || '';
      const pmName = result.paymentMethod?.name || '';
      return {
        content: [{
          type: 'text',
          text: `已記錄：${result.type === 'Income' ? '收入' : '支出'} ${result.amount} 元 - ${result.description}（${categoryName}）${pmName ? ` ${pmName}` : ''}`,
        }],
      };
    }

    case 'list_categories': {
      const { type } = args as any;
      const result = await apiCall('/api/categories?all=true');
      const items = Array.isArray(result) ? result : result.items || [];
      const filtered = type ? items.filter((c: any) => c.type.toLowerCase() === type.toLowerCase()) : items;
      return {
        content: [{ type: 'text', text: JSON.stringify(filtered.map((c: any) => ({
          id: c.id, name: c.name, type: c.type, icon: c.icon, systemCode: c.systemCode
        }))) }],
      };
    }

    case 'get_recent_transactions': {
      const { limit = 5 } = args as any;
      const result = await apiCall(`/api/transactions?limit=${limit}`);
      return {
        content: [{ type: 'text', text: JSON.stringify(Array.isArray(result) ? result : result.items || []) }],
      };
    }

    case 'undo_transaction': {
      const { id } = args as any;
      const result = await apiCall(`/api/transactions/${id}/undo`, { method: 'POST' });
      return {
        content: [{ type: 'text', text: `已復原交易 #${result.id}: ${result.description} ${result.amount} 元` }],
      };
    }

    case 'list_payment_methods': {
      const result = await apiCall('/api/payment-methods?all=true');
      const items = Array.isArray(result) ? result : result.items || [];
      return {
        content: [{ type: 'text', text: JSON.stringify(items.map((p: any) => ({
          id: p.id, name: p.name, icon: p.icon, systemCode: p.systemCode
        }))) }],
      };
    }

    case 'get_financial_summary': {
      const result = await apiCall('/api/reports/monthly-summary');
      return {
        content: [{ type: 'text', text: JSON.stringify(result) }],
      };
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
});

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error('MCP server error:', err);
  process.exit(1);
});
