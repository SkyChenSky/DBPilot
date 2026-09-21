<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { Modal, message } from 'ant-design-vue'
import { useRouter } from 'vue-router'
import { fitColumnWidth, MINUTE_COL_W } from '../../utils/fitColumnWidth'
import { fmtTimeMinute } from '../../utils/format'
import SqlText from '../../components/SqlText.vue'
import { useInstanceContext } from '../../stores/instanceContext'
import {
  listInstances,
  createInstance,
  updateInstance,
  deleteInstance,
  testUnsavedInstance,
  testInstance,
  versionLabel,
  engineLabel,
  engineBadgeColor,
  ENGINE_META,
  ENGINE_PORTS,
  type InstanceItem,
  type InstanceSaveRequest,
  type ConnectionTestResult,
} from '../../api/instance'

/** 状态映射：0未知 1在线 2退避 3离线 */
const statusMeta: Record<number, { text: string; color: string }> = {
  0: { text: '未知', color: 'default' },
  1: { text: '在线', color: 'success' },
  2: { text: '退避', color: 'warning' },
  3: { text: '离线', color: 'error' },
}

// ---------- 列表 ----------
const router = useRouter()
const instanceCtx = useInstanceContext()
const loading = ref(false)
const rows = ref<InstanceItem[]>([])
const total = ref(0)
const query = reactive({ page: 1, limit: 10, keyword: '' })

async function load() {
  loading.value = true
  try {
    const data = await listInstances({ page: query.page, limit: query.limit, keyword: query.keyword || undefined })
    rows.value = data.items
    total.value = data.total
  } finally {
    loading.value = false
  }
}

function search() {
  query.page = 1
  load()
}

const pagination = ref({
  current: query.page,
  pageSize: query.limit,
  total: 0,
  showSizeChanger: true,
  showTotal: (t: number) => `共 ${t} 条`,
})

function onPageChange(p: { current: number; pageSize: number }) {
  query.page = p.current
  query.limit = p.pageSize
  load()
}

// ---------- 新增 / 编辑弹窗 ----------
const modalOpen = ref(false)
const saving = ref(false)
const editingId = ref<number | null>(null) // null = 新增
const form = reactive<InstanceSaveRequest>({
  name: '',
  host: '',
  port: 1433,
  engine: 'sqlserver',
  loginName: '',
  password: '',
  enabled: true,
  envTag: undefined,
  slowSqlThresholdMs: 1000,
  blockingThresholdSec: 5,
  xeFilePath: undefined,
})

/** 切换引擎联动默认端口（当前端口仍是某引擎默认值时跟随切换，已手改过的端口不动） */
function onEngineChange(engine: string) {
  if (form.port != null && Object.values(ENGINE_PORTS).includes(form.port))
    form.port = ENGINE_PORTS[engine] ?? form.port
}

function openCreate() {
  editingId.value = null
  Object.assign(form, {
    name: '', host: '', port: 1433, engine: 'sqlserver', loginName: '', password: '',
    enabled: true, envTag: undefined, slowSqlThresholdMs: 1000, blockingThresholdSec: 5, xeFilePath: undefined,
  })
  testResult.value = null
  modalOpen.value = true
}

function openEdit(row: InstanceItem) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name,
    host: row.host,
    port: row.port,
    engine: row.engine || 'sqlserver',
    loginName: row.loginName,
    password: '', // 留空不修改
    enabled: row.enabled,
    envTag: row.envTag ?? undefined,
    slowSqlThresholdMs: row.slowSqlThresholdMs ?? 1000,
    blockingThresholdSec: row.blockingThresholdSec ?? 5,
    xeFilePath: row.xeFilePath ?? undefined,
  })
  testResult.value = null
  modalOpen.value = true
}

async function save() {
  if (!form.name || !form.host || !form.loginName || (!editingId.value && !form.password)) {
    message.warning('请填写名称 / 主机 / 登录名 / 密码')
    return
  }
  saving.value = true
  try {
    if (editingId.value) {
      await updateInstance(editingId.value, form)
      message.success('已保存')
    } else {
      await createInstance(form)
      message.success('实例已接入')
    }
    modalOpen.value = false
    load()
  } finally {
    saving.value = false
  }
}

// ---------- 连接测试 ----------
const testing = ref(false)
const testResult = ref<ConnectionTestResult | null>(null)

async function testForm() {
  if (!form.host || !form.loginName || !form.password) {
    message.warning('测试连接需先填写主机 / 登录名 / 密码')
    return
  }
  testing.value = true
  testResult.value = null
  try {
    testResult.value = await testUnsavedInstance(form)
  } finally {
    testing.value = false
  }
}

const rowTestingId = ref<number | null>(null)

async function testRow(row: InstanceItem) {
  rowTestingId.value = row.id
  try {
    const result = await testInstance(row.id)
    if (result.ok) {
      message.success(`连接成功（${result.latencyMs}ms）` + (result.missingPermissions.length ? '，但有缺失权限' : ''))
    } else {
      message.error(`连接失败：${result.error ?? '未知错误'}`)
    }
    load()
  } finally {
    rowTestingId.value = null
  }
}

// ---------- 启停 / 删除 ----------

/** 设为全局诊断实例并进入性能趋势（顶栏上下文随之切换，后续菜单页均作用于该实例） */
function diagnose(row: InstanceItem) {
  instanceCtx.setInstance(row.id)
  router.push('/performance/metrics')
}

async function toggleEnabled(row: InstanceItem, enabled: boolean) {
  const prev = row.enabled
  row.enabled = enabled
  try {
    await updateInstance(row.id, {
      name: row.name,
      host: row.host,
      port: row.port,
      engine: row.engine || 'sqlserver',
      loginName: row.loginName,
      password: '', // 保持不变
      enabled,
      envTag: row.envTag ?? undefined,
    })
    message.success(enabled ? '已启用监控' : '已停用监控')
  } catch {
    row.enabled = prev
  }
}

function remove(row: InstanceItem) {
  Modal.confirm({
    title: `删除实例「${row.name}」？`,
    content: '将同时删除该实例的采集配置与历史数据关联，不可恢复。',
    okText: '删除',
    okType: 'danger',
    onOk: async () => {
      await deleteInstance(row.id)
      message.success('已删除')
      load()
    },
  })
}

// 名称/地址按数据量宽带上限（约定②）

const nameW = computed(() => fitColumnWidth(rows.value.map(r => r.name), { min: 120, max: 200 }))
const addrW = computed(() => fitColumnWidth(rows.value.map(r => `${r.host}:${r.port}`), { min: 160, max: 300 }))

const columns = computed(() => [
  { title: '序号', key: 'index', width: 48, align: 'center' as const,
    customRender: ({ index }: { index: number }) => (query.page - 1) * query.limit + index + 1 },
  { title: '状态', dataIndex: 'status', key: 'status', width: 80 },
  { title: '名称', dataIndex: 'name', key: 'name', width: nameW.value },
  { title: '地址', key: 'address', width: addrW.value },
  { title: '引擎', key: 'engine', width: 96 },
  { title: '版本', key: 'version', width: 120 },
  { title: 'vCPU', dataIndex: 'cpuCores', key: 'cpuCores', width: 65 },
  { title: '环境', dataIndex: 'envTag', key: 'envTag', width: 70 },
  { title: '监控', dataIndex: 'enabled', key: 'enabled', width: 65 },
  { title: '最近心跳', key: 'heartbeat', width: MINUTE_COL_W },
  { title: '操作', key: 'actions', width: 232, fixed: 'right' as const },
])

onMounted(load)
</script>

<template>
  <div class="page">
    <div class="toolbar">
      <a-input-search
        v-model:value="query.keyword"
        placeholder="按名称 / 主机搜索"
        style="width: 260px"
        allow-clear
        @search="search"
      />
      <a-button :loading="loading" @click="load">刷新</a-button>
      <a-button class="toolbar-right" type="primary" @click="openCreate">接入实例</a-button>
    </div>

    <a-card>
      <a-table
        :columns="columns"
        :data-source="rows"
        :loading="loading"
        row-key="id"
        :pagination="{ ...pagination, total, current: query.page, pageSize: query.limit }"
        :scroll="{ x: 776 + MINUTE_COL_W + nameW + addrW }"
        @change="onPageChange"
        size="small"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'status'">
            <a-tooltip :title="record.lastError || statusMeta[record.status]?.text">
              <a-badge
                :status="(statusMeta[record.status]?.color ?? 'default') as any"
                :text="statusMeta[record.status]?.text ?? '未知'"
              />
            </a-tooltip>
          </template>
          <template v-else-if="column.key === 'address'">
            <span class="mono" :title="`${record.host}:${record.port}`">{{ record.host }}:{{ record.port }}</span>
          </template>
          <template v-else-if="column.key === 'engine'">
            <a-tag :color="engineBadgeColor(record.engine)">{{ engineLabel(record.engine) }}</a-tag>
          </template>
          <template v-else-if="column.key === 'version'">
            <a-tooltip :title="`${record.serverVersion ?? ''} ${record.edition ?? ''}`">
              {{ versionLabel(record.majorVersion, record.engine) }}
            </a-tooltip>
          </template>
          <template v-else-if="column.key === 'cpuCores'">
            {{ record.cpuCores ?? '-' }}
          </template>
          <template v-else-if="column.key === 'envTag'">
            <a-tag v-if="record.envTag" :color="record.envTag === '生产' ? 'red' : 'blue'">{{ record.envTag }}</a-tag>
            <span v-else>-</span>
          </template>
          <template v-else-if="column.key === 'enabled'">
            <a-switch size="small" :checked="record.enabled" @change="(v: any) => toggleEnabled(record, !!v)" />
          </template>
          <template v-else-if="column.key === 'heartbeat'">
            {{ fmtTimeMinute(record.lastHeartbeat) }}
          </template>
          <template v-else-if="column.key === 'actions'">
            <a-space>
              <a-button size="small" type="primary" @click="diagnose(record)">诊断</a-button>
              <a-button size="small" :loading="rowTestingId === record.id" @click="testRow(record)">测试</a-button>
              <a-button size="small" @click="openEdit(record)">编辑</a-button>
              <a-button size="small" danger @click="remove(record)">删除</a-button>
            </a-space>
          </template>
        </template>
      </a-table>
    </a-card>

    <!-- 接入 / 编辑向导 -->
    <a-modal
      v-model:open="modalOpen"
      :title="editingId ? `编辑实例「${form.name}」` : '接入实例'"
      :confirm-loading="saving"
      ok-text="保存"
      cancel-text="取消"
      width="640px"
      @ok="save"
    >
      <a-form layout="vertical" style="margin-top: 12px">
        <a-row :gutter="12">
          <a-col :span="12">
            <a-form-item label="实例名称" required>
              <a-input v-model:value="form.name" placeholder="如：订单库-生产" :maxlength="100" />
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="环境标签">
              <a-select v-model:value="form.envTag" placeholder="可选" allow-clear>
                <a-select-option value="生产">生产</a-select-option>
                <a-select-option value="测试">测试</a-select-option>
                <a-select-option value="开发">开发</a-select-option>
              </a-select>
            </a-form-item>
          </a-col>
        </a-row>
        <a-row :gutter="12">
          <a-col :span="12">
            <a-form-item label="主机地址" required>
              <a-input v-model:value="form.host" placeholder="主机名 / IP / 可用性组侦听器" />
            </a-form-item>
          </a-col>
          <a-col :span="4">
            <a-form-item label="端口" required>
              <a-input-number v-model:value="form.port" :min="1" :max="65535" style="width: 100%" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="引擎" required>
              <a-select v-model:value="form.engine" @change="onEngineChange">
                <a-select-option v-for="e in ENGINE_META" :key="e.value" :value="e.value">{{ e.label }}</a-select-option>
              </a-select>
            </a-form-item>
          </a-col>
        </a-row>
        <a-row :gutter="12">
          <a-col :span="12">
            <a-form-item label="登录名" required>
              <a-input v-model:value="form.loginName" placeholder="SQL 认证账号（建议只读采集账号）" />
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="密码" :required="!editingId">
              <a-input-password
                v-model:value="form.password"
                :placeholder="editingId ? '留空表示不修改密码' : 'AES-GCM 加密存储'"
                autocomplete="new-password"
              />
            </a-form-item>
          </a-col>
        </a-row>

        <a-collapse ghost>
          <a-collapse-panel key="adv" header="高级设置（采集阈值实例级覆盖）">
            <a-row :gutter="12">
              <a-col :span="12">
                <a-form-item label="慢 SQL 阈值（ms）">
                  <a-input-number v-model:value="form.slowSqlThresholdMs" :min="1" :max="600000" style="width: 100%" />
                </a-form-item>
              </a-col>
              <a-col :span="12">
                <a-form-item label="阻塞判定阈值（秒）">
                  <a-input-number v-model:value="form.blockingThresholdSec" :min="1" :max="3600" style="width: 100%" />
                </a-form-item>
              </a-col>
            </a-row>
            <a-form-item label="XE 文件目录（慢 SQL 捕获，留空自动探测）">
              <a-input v-model:value="form.xeFilePath" placeholder="如 D:\xe" />
            </a-form-item>
          </a-collapse-panel>
        </a-collapse>
      </a-form>

      <!-- 连接测试结果（缺失权限逐条列出影响与修复脚本） -->
      <div v-if="testResult" class="test-result">
        <a-alert
          v-if="testResult.ok"
          type="success"
          show-icon
          :message="`连接成功（${testResult.latencyMs}ms）`"
          :description="testResult.missingPermissions.length ? '连通正常，但存在缺失权限：' : '连通与权限自检全部通过'"
        />
        <a-alert v-else type="error" show-icon message="连接失败" :description="testResult.error ?? '未知错误'" />
        <div v-if="testResult.missingPermissions.length" class="perm-list">
          <div v-for="p in testResult.missingPermissions" :key="p.permission" class="perm-item">
            <a-tag color="orange">{{ p.permission }}</a-tag>
            <span class="perm-impact">{{ p.impact }}</span>
            <SqlText :text="p.fixScript" :max-height="120" />
          </div>
        </div>
      </div>

      <template #footer>
        <a-button :loading="testing" style="float: left" @click="testForm">测试连接</a-button>
        <a-button @click="modalOpen = false">取消</a-button>
        <a-button type="primary" :loading="saving" @click="save">保存</a-button>
      </template>
    </a-modal>
  </div>
</template>

<style scoped>
.mono {
  font-family: consolas, monospace;
}
.test-result {
  margin-top: 8px;
}
.perm-list {
  margin-top: 8px;
}
.perm-item {
  padding: 8px 0;
  border-bottom: 1px dashed var(--border-color-split);
}
.perm-impact {
  color: var(--text-tertiary);
  font-size: 12px;
}
</style>
