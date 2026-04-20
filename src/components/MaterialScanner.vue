<script setup lang="ts">
import { ref, watch, computed, nextTick } from 'vue'
import type { RouteStep, WorkStep } from '../types/mes'

const props = defineProps<{
  steps: RouteStep[]
  remoteCheckPassed?: boolean
}>()

const emit = defineEmits<{
  (e: 'complete', materials: { productCode: string; productCount: number }[]): void
  (e: 'single-complete', material: { productCode: string; productCount: number }): void
  (e: 'log', level: 'info' | 'success' | 'warn' | 'error', msg: string): void
}>()

export interface MaterialTask {
  uid: string
  seqIdx: number
  material_No: string
  material_Name: string
  material_number: number
  noLength: number
  retrospect_Type: unknown
  scannedCount: number
  scannedBarcodes: string[]
  status: 'pending' | 'completed'
}

const taskList = ref<MaterialTask[]>([])
const scanInput = ref('')
const inputRef = ref<HTMLInputElement | null>(null)

function buildTasks() {
  const tasks: MaterialTask[] = []
  let uidCounter = 0

  props.steps.forEach((seq, si) => {
    const wsList = (seq.workStepList as WorkStep[]) || []
    wsList.forEach((ws) => {
      const matList = (ws.workStepMaterialList as any[]) || []
      matList.forEach((mat) => {
        const reqNum = Number(mat.material_number) || 0
        const materialNo = String(mat.material_No ?? '').trim()
        if (!materialNo || reqNum <= 0) return

        tasks.push({
          uid: `mat-${uidCounter++}`,
          seqIdx: si + 1,
          material_No: materialNo,
          material_Name: String(mat.material_Name ?? ''),
          material_number: reqNum,
          noLength: Number(mat.noLength) || 0,
          retrospect_Type: mat.retrospect_Type,
          scannedCount: 0,
          scannedBarcodes: [],
          status: 'pending'
        })
      })
    })
  })

  taskList.value = tasks
}

watch(
  () => props.steps,
  () => {
    buildTasks()
    if (taskList.value.length > 0) {
      nextTick(() => inputRef.value?.focus())
    }
  },
  { immediate: true, deep: true }
)

const isAllCompleted = computed(() => {
  if (taskList.value.length === 0) return false
  return taskList.value.every((t) => t.status === 'completed')
})

const isFinalPassed = computed(() => isAllCompleted.value && props.remoteCheckPassed === true)

function normalizeCode(raw: string) {
  return raw.trim().toUpperCase()
}

function handleScan() {
  const code = normalizeCode(scanInput.value)
  if (!code) return

  const isDuplicate = taskList.value.some((t) => t.scannedBarcodes.includes(code))
  if (isDuplicate) {
    emit('log', 'warn', `扫码重复，已清空当前条码: ${code}`)
    scanInput.value = ''
    return
  }

  const candidates = taskList.value
    .filter((t) => {
      if (t.status === 'completed') return false

      const prefix = normalizeCode(t.material_No)
      if (!prefix || !code.startsWith(prefix)) return false
      if (t.noLength > 0 && code.length < t.noLength) return false

      return true
    })
    .sort((a, b) => normalizeCode(b.material_No).length - normalizeCode(a.material_No).length)

  const target = candidates[0]

  if (!target) {
    emit('log', 'error', `扫码无匹配物料或该物料已扫完: ${code}`)
    scanInput.value = ''
    return
  }

  target.scannedCount++
  target.scannedBarcodes.push(code)

  if (target.scannedCount >= target.material_number) {
    target.status = 'completed'
    emit('log', 'success', `物料扫描匹配成功: ${target.material_Name} (全部完成)`)
  } else {
    emit('log', 'success', `物料扫描匹配成功: ${target.material_Name} (${target.scannedCount}/${target.material_number})`)
  }

  emit('single-complete', { productCode: code, productCount: 1 })

  if (isAllCompleted.value) {
    emit('log', 'success', '所有物料校验已全部通过')

    const payloadMats = taskList.value.flatMap((t) =>
      t.scannedBarcodes.map((scannedCode) => ({
        productCode: scannedCode,
        productCount: 1
      }))
    )

    emit('complete', payloadMats)
  }

  scanInput.value = ''
}
</script>

<template>
  <div class="material-scanner-panel">
    <div class="scan-action-bar">
      <div class="scan-input-wrapper">
        <span class="icon">扫码</span>
        <input
          ref="inputRef"
          type="text"
          class="scan-input"
          v-model="scanInput"
          @keyup.enter="handleScan"
          placeholder="请使用扫码枪扫描物料条码以验证组件..."
          autocomplete="off"
          :disabled="isAllCompleted || !taskList.length"
        />
        <button
          class="submit-btn"
          @click="handleScan"
          :disabled="isAllCompleted || !taskList.length"
        >验证</button>
      </div>

      <div class="progress-status" v-if="taskList.length">
        状态:
        <span v-if="isFinalPassed" class="status-all-done">全部验证通过</span>
        <span v-else-if="isAllCompleted" class="status-pending">本地扫码完成，等待全物料校验</span>
        <span v-else class="status-pending">等待验证 ({{ taskList.filter(t => t.status === 'completed').length }}/{{ taskList.length }})</span>
      </div>
    </div>

    <div v-if="!taskList.length" class="empty-state">
      当前工步无物料绑定信息，无需扫描验证。
    </div>

    <div v-else class="table-scroll">
      <table>
        <thead>
          <tr>
            <th style="width: 40px">序号</th>
            <th>物料编号</th>
            <th>物料名称</th>
            <th style="width: 60px" class="center">需求数</th>
            <th style="width: 60px" class="center">条码长度</th>
            <th style="width: 80px" class="center">追溯类型</th>
            <th style="width: 80px" class="center">已扫数量</th>
            <th style="width: 60px" class="center">状态</th>
            <th>已匹配条码</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="(task, idx) in taskList"
            :key="task.uid"
            class="data-row"
            :class="{ 'done-row': task.status === 'completed' }"
          >
            <td>
              <span class="seq-badge" :class="{ 'done-badge': task.status === 'completed' }">{{ idx + 1 }}</span>
            </td>
            <td class="mono c-blue">{{ task.material_No }}</td>
            <td class="mat-name">{{ task.material_Name }}</td>
            <td class="center req-num">{{ task.material_number }}</td>
            <td class="center">{{ task.noLength > 0 ? task.noLength : '-' }}</td>
            <td class="center">{{ task.retrospect_Type ?? '-' }}</td>

            <td class="center">
              <span class="scan-count" :class="{ 'full': task.scannedCount >= task.material_number, 'partial': task.scannedCount > 0 && task.scannedCount < task.material_number }">
                {{ task.scannedCount }}
              </span>
            </td>

            <td class="center">
              <span v-if="task.status === 'completed'" class="status-tag success">通过</span>
              <span v-else class="status-tag pending">待扫</span>
            </td>

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

.scan-input-wrapper {
  flex: 1;
  max-width: 500px;
  display: flex;
  align-items: center;
  background: #0d1117;
  border: 1px solid rgba(100, 181, 246, 0.3);
  border-radius: 6px;
  padding: 4px 6px;
}

.scan-input-wrapper:focus-within {
  border-color: #42a5f5;
  box-shadow: 0 0 0 2px rgba(66, 165, 245, 0.2);
}

.scan-input-wrapper .icon {
  margin: 0 8px;
  opacity: 0.6;
}

.scan-input {
  flex: 1;
  background: transparent;
  border: none;
  color: #e3f2fd;
  font-family: inherit;
  font-size: 14px;
  outline: none;
}

.scan-input:disabled {
  opacity: 0.5;
}

.submit-btn {
  background: #1976d2;
  color: white;
  border: none;
  padding: 6px 14px;
  border-radius: 4px;
  cursor: pointer;
  font-weight: 600;
  transition: all 0.2s;
}

.submit-btn:hover:not(:disabled) {
  background: #1565c0;
}

.submit-btn:disabled {
  background: #37474f;
  color: #78909c;
  cursor: not-allowed;
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

.done-row {
  background: rgba(0, 230, 118, 0.03);
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

.done-badge {
  background: #00e676;
  color: #000;
}

.scan-count {
  font-size: 13px;
  font-weight: 700;
  color: #78909c;
}

.scan-count.partial {
  color: #ffab40;
}

.scan-count.full {
  color: #00e676;
}

.status-tag {
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 10px;
  font-weight: 600;
}

.status-tag.pending {
  background: rgba(255, 171, 64, 0.15);
  color: #ffab40;
}

.status-tag.success {
  background: rgba(0, 230, 118, 0.15);
  color: #00e676;
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
