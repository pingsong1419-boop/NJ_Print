<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type { AppConfig } from '../types/mes'

const props = defineProps<{
  modelValue: AppConfig
  visible: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', val: AppConfig): void
  (e: 'update:visible', val: boolean): void
  (e: 'save'): void
}>()

const form = reactive<AppConfig>({ ...props.modelValue })
type PrintPathField = 'barTenderTemplatePath1' | 'barTenderTemplatePath2' | 'barTenderDatabasePath1' | 'barTenderDatabasePath2' | 'barTenderExePath'
const selectingField = ref<PrintPathField | 'barTenderExePath' | ''>('')

watch(
  () => props.visible,
  (visible) => {
    if (visible) Object.assign(form, props.modelValue)
  }
)

function handleSave() {
  emit('update:modelValue', {
    ...form,
    barTenderTemplatePath: form.barTenderTemplatePath1,
    barTenderDatabasePath: form.barTenderDatabasePath1
  })
  emit('save')
  emit('update:visible', false)
}

function handleCancel() {
  Object.assign(form, props.modelValue)
  emit('update:visible', false)
}

async function pickPath(field: PrintPathField, target: 'template' | 'database' | 'bartenderexe') {
  if (selectingField.value) return

  selectingField.value = field
  try {
    const response = await fetch('/pathPicker/select', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify({ target })
    })

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    const data = (await response.json()) as { success?: boolean; cancelled?: boolean; path?: string; message?: string }
    if (data.cancelled) return
    if (!data.success || !data.path) {
      throw new Error(data.message || '未返回路径')
    }

    form[field] = String(data.path).trim().replace(/\//g, '\\')
  } catch (err: any) {
    alert(`选择路径失败: ${err?.message || String(err)}`)
  } finally {
    selectingField.value = ''
  }
}
</script>

<template>
  <Transition name="modal">
    <div v-if="visible" class="modal-overlay" @click.self="handleCancel">
      <div class="modal-panel">
        <div class="modal-header">
          <h2>系统配置</h2>
          <button class="close-btn" @click="handleCancel">关闭</button>
        </div>

        <div class="modal-body">
          <section class="config-section">
            <div class="section-title">MES 参数设置</div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>MES API 基地址 (目标服务器)</label>
                <input v-model="form.mesApiBaseUrl" type="text" class="input-field" placeholder="http://172.25.57.144:8076" />
              </div>
              <div class="field-group">
                <label>MES Push 基地址 (推送服务)</label>
                <input v-model="form.mesPushBaseUrl" type="text" class="input-field" placeholder="http://172.25.57.144:8072" />
              </div>
            </div>
            <div class="field-group">
              <label>首工序获取工单 API</label>
              <input v-model="form.orderApiUrl" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>获取工步 API</label>
              <input v-model="form.routeApiUrl" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>单物料校验 API</label>
              <input v-model="form.singleMaterialApiUrl" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>全物料校验 API</label>
              <input v-model="form.fullMaterialApiUrl" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>获取条码 API</label>
              <input v-model="form.codeCreateApiUrl" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>MES 数据上传 API</label>
              <input v-model="form.mesPushApiUrl" type="text" class="input-field" />
            </div>
          </section>

          <section class="config-section">
            <div class="section-title">系统参数设置</div>
            <div class="field-group">
              <label>工序代码 (technicsProcessCode)</label>
              <input v-model="form.technicsProcessCode" type="text" class="input-field" />
            </div>
            <div class="field-group">
              <label>工序名称 (technicsProcessName)</label>
              <input v-model="form.technicsProcessName" type="text" class="input-field" />
            </div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>用户名 (userName)</label>
                <input v-model="form.userName" type="text" class="input-field" />
              </div>
              <div class="field-group">
                <label>用户账号 (userAccount)</label>
                <input v-model="form.userAccount" type="text" class="input-field" />
              </div>
            </div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>设备编码 (deviceCode)</label>
                <input v-model="form.deviceCode" type="text" class="input-field" />
              </div>
              <div class="field-group">
                <label>设备名称 (deviceName)</label>
                <input v-model="form.deviceName" type="text" class="input-field" />
              </div>
            </div>
            <div class="field-group">
              <label>日志保存路径 (后台机器)</label>
              <input v-model="form.logSavePath" type="text" class="input-field" />
            </div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>管理员账号</label>
                <input v-model="form.adminUsername" type="text" class="input-field" />
              </div>
              <div class="field-group">
                <label>管理员密码</label>
                <input v-model="form.adminPassword" type="password" class="input-field" />
              </div>
            </div>
          </section>

          <section class="config-section">
            <div class="section-title">打印机参数设置</div>
            <div class="field-group">
              <label>BarTender EXE 路径</label>
              <div class="path-row">
                <input v-model="form.barTenderExePath" type="text" class="input-field" />
                <button class="path-btn" :disabled="!!selectingField" @click="pickPath('barTenderExePath', 'bartenderexe')">
                  {{ selectingField === 'barTenderExePath' ? '选择中...' : '选择' }}
                </button>
              </div>
            </div>
            <div class="field-group">
              <label>模板路径 1 (.btw)</label>
              <div class="path-row">
                <input v-model="form.barTenderTemplatePath1" type="text" class="input-field" />
                <button class="path-btn" :disabled="!!selectingField" @click="pickPath('barTenderTemplatePath1', 'template')">
                  {{ selectingField === 'barTenderTemplatePath1' ? '选择中...' : '选择' }}
                </button>
              </div>
            </div>
            <div class="field-group">
              <label>模板路径 2 (.btw)</label>
              <div class="path-row">
                <input v-model="form.barTenderTemplatePath2" type="text" class="input-field" />
                <button class="path-btn" :disabled="!!selectingField" @click="pickPath('barTenderTemplatePath2', 'template')">
                  {{ selectingField === 'barTenderTemplatePath2' ? '选择中...' : '选择' }}
                </button>
              </div>
            </div>
            <div class="field-group">
              <label>数据库路径 1 (CSV/文本)</label>
              <div class="path-row">
                <input v-model="form.barTenderDatabasePath1" type="text" class="input-field" />
                <button class="path-btn" :disabled="!!selectingField" @click="pickPath('barTenderDatabasePath1', 'database')">
                  {{ selectingField === 'barTenderDatabasePath1' ? '选择中...' : '选择' }}
                </button>
              </div>
            </div>
            <div class="field-group">
              <label>数据库路径 2 (CSV/文本)</label>
              <div class="path-row">
                <input v-model="form.barTenderDatabasePath2" type="text" class="input-field" />
                <button class="path-btn" :disabled="!!selectingField" @click="pickPath('barTenderDatabasePath2', 'database')">
                  {{ selectingField === 'barTenderDatabasePath2' ? '选择中...' : '选择' }}
                </button>
              </div>
            </div>
          </section>

          <section class="config-section">
            <div class="section-title">得力捷扫码枪参数</div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>扫码枪 IP</label>
                <input v-model="form.scannerIp" type="text" class="input-field" />
              </div>
              <div class="field-group">
                <label>扫码枪端口</label>
                <input v-model.number="form.scannerPort" type="number" class="input-field" />
              </div>
            </div>
            <div class="field-groups-row">
              <div class="field-group">
                <label>条码匹配规则 (Regex)</label>
                <input v-model="form.barcodeMatchRegex" type="text" class="input-field" />
              </div>
            </div>
          </section>
        </div>

        <div class="modal-footer">
          <button class="btn-cancel" @click="handleCancel">取消</button>
          <button class="btn-save" @click="handleSave">保存</button>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.65);
  backdrop-filter: blur(4px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.modal-panel {
  background: #1a1f2e;
  border: 1px solid rgba(100, 181, 246, 0.25);
  border-radius: 12px;
  width: 560px;
  max-width: 95vw;
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.5);
  overflow: hidden;
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 18px 24px;
  background: linear-gradient(135deg, #0d47a1 0%, #1565c0 100%);
  border-bottom: 1px solid rgba(100, 181, 246, 0.2);
}

.modal-header h2 {
  font-size: 16px;
  font-weight: 600;
  color: #e3f2fd;
  margin: 0;
}

.close-btn {
  background: transparent;
  border: 1px solid rgba(255, 255, 255, 0.25);
  color: #e3f2fd;
  border-radius: 4px;
  padding: 4px 8px;
  cursor: pointer;
}

.modal-body {
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  max-height: 70vh;
  overflow: auto;
}

.config-section {
  border: 1px solid rgba(100, 181, 246, 0.16);
  border-radius: 10px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  background: rgba(13, 17, 23, 0.45);
}

.section-title {
  font-size: 14px;
  font-weight: 700;
  color: #90caf9;
  border-left: 3px solid #42a5f5;
  padding-left: 8px;
}

.field-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.field-group label {
  font-size: 13px;
  font-weight: 600;
  color: #90caf9;
}

.input-field {
  background: #0d1117;
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 6px;
  color: #e0e6ed;
  padding: 10px 14px;
  font-size: 13px;
  font-family: Consolas, monospace;
  outline: none;
}

.input-field:focus {
  border-color: #42a5f5;
  box-shadow: 0 0 0 3px rgba(66, 165, 245, 0.15);
}

.path-row {
  display: flex;
  gap: 8px;
  align-items: center;
}

.path-row .input-field {
  flex: 1;
}

.path-btn {
  height: 40px;
  min-width: 78px;
  border-radius: 6px;
  border: 1px solid rgba(100, 181, 246, 0.3);
  background: rgba(21, 101, 192, 0.18);
  color: #e3f2fd;
  cursor: pointer;
}

.path-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.field-groups-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.modal-footer {
  padding: 16px 24px;
  display: flex;
  gap: 12px;
  justify-content: flex-end;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
}

.btn-cancel {
  padding: 9px 20px;
  background: transparent;
  border: 1px solid rgba(100, 181, 246, 0.25);
  border-radius: 6px;
  color: #78909c;
  font-size: 13px;
  cursor: pointer;
}

.btn-save {
  padding: 9px 24px;
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  border: 1px solid rgba(100, 181, 246, 0.3);
  border-radius: 6px;
  color: #e3f2fd;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}

.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.25s ease;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-active .modal-panel,
.modal-leave-active .modal-panel {
  transition: transform 0.25s ease;
}

.modal-enter-from .modal-panel,
.modal-leave-to .modal-panel {
  transform: scale(0.92) translateY(-20px);
}
</style>
