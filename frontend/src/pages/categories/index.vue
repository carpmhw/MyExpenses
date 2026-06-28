<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { Category } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import IconPicker from '../../components/ui/IconPicker.vue'
import Icon from '../../components/ui/Icon.vue'
import { usePagination } from '../../composables/usePagination'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const categories = ref<Category[]>([])
const loading = ref(false)
const saving = ref(false)
const pagination = usePagination(1, 15)

const modalOpen = ref(false)
const editingItem = ref<Category | null>(null)
const form = ref({ name: '', type: 'Expense' as 'Income' | 'Expense', icon: '', color: '#6366F1', sortOrder: 0 })

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)
const restoreConfirmOpen = ref(false)
const restoring = ref(false)

const columns = [
  { key: 'name', label: '名稱' },
  { key: 'type', label: '類型' },
  { key: 'icon', label: '圖示' },
  { key: 'color', label: '顏色' },
  { key: 'sortOrder', label: '排序' },
]

const typeOptions = [
  { value: 'Income', label: '收入' },
  { value: 'Expense', label: '支出' },
]

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.name?.trim()) errs.name = '請填寫名稱'
  if (!form.value.color?.trim()) errs.color = '請選擇顏色'
  return errs
})

async function fetchList() {
  loading.value = true
  try {
    const result = await api.categories.list({ page: pagination.page.value, pageSize: pagination.pageSize.value })
    categories.value = result.items
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingItem.value = null
  form.value = { name: '', type: 'Expense', icon: '', color: '#6366F1', sortOrder: 0 }
  modalOpen.value = true
}

function openEdit(item: Category) {
  editingItem.value = item
  form.value = { name: item.name, type: item.type, icon: item.icon, color: item.color, sortOrder: item.sortOrder }
  modalOpen.value = true
}

async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  try {
    if (editingItem.value) {
      await api.categories.update(editingItem.value.id, form.value)
      toast.success('分類已更新')
    } else {
      await api.categories.create(form.value)
      toast.success('分類已建立')
    }
    modalOpen.value = false
    await fetchList()
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }
}

function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.categories.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('分類已刪除')
      await fetchList()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

async function doRestoreDefaults() {
  restoring.value = true
  try {
    const result = await api.categories.restoreDefaults()
    categories.value = result.items
    pagination.total.value = result.total
    restoreConfirmOpen.value = false
    toast.success('分類已還原為系統預設')
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '還原失敗')
  } finally {
    restoring.value = false
  }
}

onMounted(fetchList)

watch(() => pagination.page.value, () => fetchList())
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">分類管理</h1>
        <p class="text-xs text-text-secondary mt-1">收入與支出分類 · Categories</p>
      </div>
      <div class="flex items-center gap-2">
        <Button variant="warning" :loading="restoring" @click="restoreConfirmOpen = true">還原系統預設</Button>
        <Button @click="openCreate">+ 新增分類</Button>
      </div>
    </div>

    <Card>
      <DataTable :columns="columns" :loading="loading" :items="categories">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無分類資料</div>
        </template>
        <tr v-for="item in categories" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 w-[180px]">
            <div class="flex items-center gap-2">
              <Icon :name="item.icon" :color="item.color" :size="18" />
              <span class="text-text-primary font-medium">{{ item.name }}</span>
            </div>
          </td>
          <td class="py-3 px-4 w-[80px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium"
              :class="item.type === 'Income' ? 'bg-green-100 dark:bg-green-900 text-green-700 dark:text-green-300' : 'bg-red-100 dark:bg-red-900 text-red-700 dark:text-red-300'"
            >
              {{ item.type === 'Income' ? '收入' : '支出' }}
            </span>
          </td>
          <td class="py-3 px-4 text-text-secondary w-[100px]">{{ item.icon }}</td>
          <td class="py-3 px-4 w-[60px]">
            <span class="inline-block w-5 h-5 rounded-full border border-border-default" :style="{ backgroundColor: item.color }" />
          </td>
          <td class="py-3 px-4 text-text-secondary w-[60px]">{{ item.sortOrder }}</td>
          <td class="py-3 px-4 w-[80px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-red-500 cursor-pointer transition-colors"
                @click="confirmDelete(item.id)"
              >
                <Icon name="trash-2" :size="16" />
              </button>
            </div>
          </td>
        </tr>
      </DataTable>

      <div class="flex items-center justify-between px-4 py-3 border-t border-border-default">
        <span class="text-sm text-text-secondary">共 {{ pagination.total.value }} 筆</span>
        <div class="flex items-center gap-2">
          <Button variant="ghost" :disabled="!pagination.hasPrev.value" @click="pagination.prev()">上一頁</Button>
          <span class="text-sm text-text-secondary">{{ pagination.page.value }} / {{ pagination.totalPages.value }}</span>
          <Button variant="ghost" :disabled="!pagination.hasNext.value" @click="pagination.next()">下一頁</Button>
        </div>
      </div>
    </Card>

    <Modal :open="modalOpen" :title="editingItem ? '編輯分類' : '新增分類'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">名稱</label>
          <Input v-model="form.name" :error="formErrors.name" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">類型</label>
          <Select v-model="form.type" :options="typeOptions" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">圖示</label>
          <IconPicker v-model="form.icon" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">顏色</label>
          <div class="flex items-center gap-3">
            <input
              v-model="form.color"
              type="color"
              class="w-10 h-10 rounded-lg border border-border-default cursor-pointer"
            />
            <span class="text-sm text-text-secondary">{{ form.color }}</span>
          </div>
          <p v-if="formErrors.color" class="mt-1 text-xs text-red-500">{{ formErrors.color }}</p>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">排序</label>
          <Input
            :model-value="String(form.sortOrder)"
            type="number"
            @update:model-value="form.sortOrder = Number($event) || 0"
          />
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除分類"
      description="確定要刪除此分類嗎？相關的交易資料不會被刪除，但分類會失效。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />

    <ConfirmDialog
      :open="restoreConfirmOpen"
      title="還原系統預設"
      description="此操作將覆蓋系統預設分類的異動，並新增尚未建立的系統分類。你自行建立的分類不會受影響。確定要繼續嗎？"
      variant="warning"
      confirm-text="確定還原"
      @update:open="restoreConfirmOpen = $event"
      @confirm="doRestoreDefaults"
    />
  </div>
</template>
