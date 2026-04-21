<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import type { RouteStep, WorkStep } from '../types/mes'

const props = defineProps<{
  steps: RouteStep[]
  remoteCheckPassed?: boolean
  productCode?: string
}>()

const emit = defineEmits<{
  (e: 'complete', materials: { productCode: string; productCount: number }[]): void
  (e: 'log', level: 'info' | 'success' | 'warn' | 'error', msg: string): void
}>()

interface MaterialTask {
  uid: string
  material_No: string
  material_Name: string
  material_number: number
  retrospect_Type: unknown
  scannedBarcodes: string[]
}

const taskList = ref<MaterialTask[]>([])
const autoSubmittedCode = ref('')

function normalizeCode(raw: string) {
  return raw.trim().toUpperCase()
}

const startProductCode = computed(() => normalizeCode(props.productCode || ''))

function buildTasks() {
  const tasks: MaterialTask[] = []
  let uidCounter = 0

  props.steps.forEach((seq) => {
    const wsList = (seq.workStepList as WorkStep[]) || []
    wsList.forEach((ws) => {
      const matList = (ws.workStepMaterialList as any[]) || []
      matList.forEach((mat) => {
        const reqNum = Number(mat.material_number) || 0
        const materialNo = String(mat.material_No ?? '').trim()
        if (!materialNo || reqNum <= 0) return

        tasks.push({
          uid: `mat-${uidCounter++}`,
          material_No: materialNo,
          material_Name: String(mat.material_Name ?? ''),
          material_number: reqNum,
          retrospect_Type: mat.retrospect_Type,
          scannedBarcodes: []
        })
      })
    })
  })

  taskList.value = tasks
  autoSubmittedCode.value = ''
}

function autoSubmitStartBarcode() {
  const code = startProductCode.value
  if (!code || !taskList.value.length) return
  if (code === autoSubmittedCode.value) return

  autoSubmittedCode.value = code
  taskList.value.forEach((t) => {
    t.scannedBarcodes = [code]
  })

  emit('complete', [{ productCode: code, productCount: 1 }])
}

watch(
  () => props.steps,
  () => {
    buildTasks()
    autoSubmitStartBarcode()
  },
  { immediate: true, deep: true }
)

watch(
  () => props.productCode,
  () => {
    autoSubmitStartBarcode()
  }
)

const isSubmitted = computed(() => !!autoSubmittedCode.value)
const isFinalPassed = computed(() => isSubmitted.value && props.remoteCheckPassed === true)
</script>

<template>
  <div class="material-scanner-panel">
    <div class="scan-action-bar">
      <div class="auto-check-tip">
        物料校验自动模式（无本地适配）：使用开始产品条码
        <span class="barcode-chip mono">{{ startProductCode || '--' }}</span>
      </div>

      <div class="progress-status" v-if="taskList.length">
        状态:
        <span v-if="isFinalPassed" class="status-all-done">全部验证通过</span>
        <span v-else-if="isSubmitted" class="status-pending">已提交MES校验，等待返回</span>
        <span v-else class="status-pending">等待开始产品条码</span>
      </div>
    </div>

    <div v-if="!taskList.length" class="empty-state">当前工步无物料绑定信息，无需校验。</div>

    <div v-else class="table-scroll">
      <table>
        <thead>
          <tr>
            <th style="width: 40px">序号</th>
            <th>物料编号</th>
            <th>物料名称</th>
            <th style="width: 60px" class="center">需求数</th>
            <th style="width: 80px" class="center">追溯类型</th>
            <th>提交条码</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(task, idx) in taskList" :key="task.uid" class="data-row">
            <td>
              <span class="seq-badge">{{ idx + 1 }}</span>
            </td>
            <td class="mono c-blue">{{ task.material_No }}</td>
            <td class="mat-name">{{ task.material_Name }}</td>
            <td class="center req-num">{{ task.material_number }}</td>
            <td class="center">{{ task.retrospect_Type ?? '-' }}</td>
            <td class="barcodes-cell mono small">
              <div v-for="(code, i) in task.scannedBarcodes" :key="i" class="code-item">
                {{ code }}
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.material-scanner-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.scan-action-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 14px;
  background: rgba(13, 71, 161, 0.15);
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  gap: 16px;
  flex-shrink: 0;
}

.auto-check-tip {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #90caf9;
  font-size: 13px;
}

.barcode-chip {
  display: inline-block;
  background: rgba(66, 165, 245, 0.15);
  border: 1px solid rgba(66, 165, 245, 0.28);
  color: #80cbc4;
  border-radius: 6px;
  padding: 3px 8px;
  max-width: 340px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.progress-status {
  font-size: 13px;
  font-weight: 600;
}

.status-all-done {
  color: #00e676;
  animation: pulse 2s infinite;
}

.status-pending {
  color: #ffab40;
}

.empty-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #546e7a;
  font-size: 13px;
}

.table-scroll {
  flex: 1;
  overflow: auto;
}

.table-scroll::-webkit-scrollbar {
  width: 4px;
  height: 4px;
}

.table-scroll::-webkit-scrollbar-thumb {
  background: rgba(100, 181, 246, 0.2);
}

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

thead tr {
  background: rgba(21, 101, 192, 0.2);
  position: sticky;
  top: 0;
  z-index: 2;
}

th {
  padding: 8px 12px;
  text-align: left;
  color: #78909c;
  font-weight: 600;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  white-space: nowrap;
}

.data-row {
  border-bottom: 1px solid rgba(100, 181, 246, 0.05);
  transition: background 0.15s;
}

.data-row:hover {
  background: rgba(66, 165, 245, 0.04);
}

td {
  padding: 8px 12px;
  color: #cfd8dc;
  vertical-align: middle;
}

.center {
  text-align: center;
}

.mono {
  font-family: 'Consolas', monospace;
}

.small {
  font-size: 11px;
}

.c-blue {
  color: #64b5f6;
}

.mat-name {
  font-weight: 500;
  color: #e0e6ed;
}

.req-num {
  font-weight: 600;
  color: #90caf9;
}

.seq-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  background: rgba(100, 181, 246, 0.15);
  border-radius: 4px;
  font-size: 10px;
  color: #90caf9;
}

.code-item {
  color: #80cbc4;
  background: rgba(128, 203, 196, 0.1);
  padding: 1px 6px;
  border-radius: 4px;
  margin-bottom: 2px;
  display: inline-block;
}

.code-item:last-child {
  margin-bottom: 0;
}

@keyframes pulse {
  0%,
  100% {
    opacity: 1;
  }

  50% {
    opacity: 0.6;
  }
}
</style>
