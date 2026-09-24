<script setup lang="ts">
import { computed, nextTick, onActivated, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import dayjs from 'dayjs'
import type { ECharts } from 'echarts/core'
import { getDeadlockDetail, type DeadlockDetail as Detail, type DeadlockProcess } from '../../api/deadlock'
import SqlText from '../../components/SqlText.vue'
import { initChart } from '../../charts/echarts'
import { palette } from '../../theme/palette'
import { fmtMsUnit, fmtTime } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

const route = useRoute()
const router = useRouter()

// 引擎守卫（能力矩阵）：死锁事件明细仅支持有事件数据源的引擎——无数据源时不发请求直接空态（防深链直达）
const { cap } = useEngineCaps()
const noEvents = computed(() => cap(CAP.deadlockEvents) === 'none')

const loading = ref(true)
const detail = ref<Detail>()
const chartEl = ref<HTMLDivElement>()
let chart: ECharts | null = null

/** 执行栈帧清洗：帧文本 unknown（adhoc 批普遍形态，引擎取不到语句文本）只留 procname；
 * 整栈只剩 adhoc 时零信息量，返回 null 隐藏区块 —— 存储过程内死锁会带完整 procname+语句。 */
function frameLines(frames: string[]): string[] | null {
  const lines = frames.map(f => (f.endsWith(': unknown') ? f.slice(0, -': unknown'.length) : f))
  return lines.some(l => l !== 'adhoc') ? lines : null
}

/** 事务时长：死锁时刻 - 事务开始（近似，XE 本地时间分钟级 offset 转换） */
function tranDur(p: DeadlockProcess) {
  if (!p.lastTranStartedUtc || !detail.value) return null
  const sec = dayjs(detail.value.eventTimeUtc).diff(dayjs(p.lastTranStartedUtc), 'second')
  return sec >= 0 ? fmtMsUnit(sec * 1000) : null
}

// ---------- 死锁关系图（环形布局：沿等待环铺圆周，任意 N 方死锁连线零交叉） ----------
const ResourceMeta: Record<string, { symbol: string; label: string }> = {
  keylock: { symbol: 'diamond', label: '键锁 KEY' },
  ridlock: { symbol: 'diamond', label: '行锁 RID' },
  pagelock: { symbol: 'rect', label: '页锁 PAGE' },
  objectlock: { symbol: 'roundRect', label: '对象锁 OBJECT' },
  exchangeEvent: { symbol: 'triangle', label: '并行交换' },
  databaselock: { symbol: 'rect', label: '库锁 DATABASE' },
}

function renderGraph() {
  if (!chartEl.value || !detail.value) return
  chart?.dispose()

  const procs = detail.value.processes
  const rescs = detail.value.resources

  // ---- 环序遍历：从牺牲进程沿 申请→资源→持有者 走一圈（死锁 = 等待环，环序铺圆周连线零交叉） ----
  const ordered: string[] = []
  const seen = new Set<string>()
  let cur: string | undefined = (procs.find(p => p.isVictim) ?? procs[0])?.id
  while (cur && !seen.has(cur)) {
    seen.add(cur)
    ordered.push(cur)
    const proc = procs.find(p => p.id === cur)
    const res = proc && rescs.find(r => r.waiters.some(w => w.processId === proc.id))
    if (!res || seen.has(res.id)) break
    seen.add(res.id)
    ordered.push(res.id)
    cur = res.owners[0]?.processId
  }
  for (const p of procs) if (!seen.has(p.id)) { seen.add(p.id); ordered.push(p.id) }
  for (const r of rescs) if (!seen.has(r.id)) { seen.add(r.id); ordered.push(r.id) }

  const N = ordered.length
  const W = chartEl.value.clientWidth
  const H = N <= 4 ? 480 : Math.min(760, 160 + 120 * N)
  chartEl.value.style.height = `${H}px`
  chart = initChart(chartEl.value)

  const R = Math.min(H / 2 - 120, W / 2 - 220)
  const posOf = new Map<string, { x: number; y: number; deg: number }>()
  ordered.forEach((id, i) => {
    const deg = -90 + (360 / N) * i
    const rad = (deg * Math.PI) / 180
    posOf.set(id, { x: W / 2 + R * Math.cos(rad), y: H / 2 + R * Math.sin(rad), deg })
  })

  /** 标签朝外：右侧节点标签在右、左侧在左、上/下同理 */
  const outward = (deg: number): { position: 'left' | 'right' | 'top' | 'bottom'; align: 'left' | 'right' | 'center' } => {
    const a = ((deg % 360) + 360) % 360
    if (a > 315 || a <= 45) return { position: 'right', align: 'left' }
    if (a > 135 && a <= 225) return { position: 'left', align: 'right' }
    return { position: a > 45 && a <= 135 ? 'bottom' : 'top', align: 'center' }
  }

  const nodes: any[] = []
  const links: any[] = []

  for (const p of procs) {
    const pos = posOf.get(p.id)!
    const o = outward(pos.deg)
    nodes.push({
      id: p.id,
      x: pos.x,
      y: pos.y,
      name: String(p.spid),
      symbolSize: 46,
      itemStyle: {
        color: p.isVictim ? palette.danger.bg : palette.brand.primaryBg,
        borderColor: p.isVictim ? palette.danger.text : palette.brand.primary,
        borderWidth: p.isVictim ? 4 : 2,
      },
      raw: p,
      kind: 'process',
      label: {
        show: true,
        position: o.position,
        align: o.align,
        distance: 10,
        formatter: `{spid|会话 ${p.spid}}${p.isVictim ? '{v| 牺牲者 }' : ''}\n{sub|${`${p.loginName ?? '-'}@${p.hostName ?? '-'}`.slice(0, 20)}}\n{sub|${(p.clientApp ?? '').slice(0, 14)}}`,
        rich: {
          spid: { fontSize: 15, fontWeight: 'bold', color: p.isVictim ? palette.danger.text : palette.brand.primary, lineHeight: 20 },
          v: { fontSize: 10, color: '#fff', backgroundColor: palette.danger.text, padding: [1, 4], borderRadius: 3 },
          sub: { fontSize: 10, color: palette.text.tertiary, lineHeight: 14 },
        },
      },
    })
  }

  for (const r of rescs) {
    const pos = posOf.get(r.id)!
    const o = outward(pos.deg)
    const meta = ResourceMeta[r.resourceType] ?? { symbol: 'diamond', label: r.resourceType }
    const disp = r.display.length > 24 ? `${r.display.slice(0, 24)}…` : r.display
    nodes.push({
      id: r.id,
      x: pos.x,
      y: pos.y,
      name: disp,
      symbol: meta.symbol,
      symbolSize: [56, 40],
      itemStyle: { color: palette.success.bg, borderColor: palette.success.text, borderWidth: 2 },
      raw: r,
      kind: 'resource',
      label: {
        show: true,
        position: o.position,
        align: o.align,
        distance: 6,
        formatter: `{t|${meta.label}}\n{n|${disp}}`,
        rich: {
          t: { fontSize: 10, color: palette.success.text, lineHeight: 14 },
          n: { fontSize: 11, color: palette.text.primary, lineHeight: 15 },
        },
      },
    })
    for (const w of r.waiters)
      links.push({
        source: w.processId, target: r.id,
        symbol: ['none', 'arrow'], symbolSize: 10,
        lineStyle: { color: palette.brand.primary, width: 2 },
        label: {
          show: true, formatter: w.mode, fontSize: 11, fontWeight: 'bold', color: palette.brand.primary,
          backgroundColor: palette.brand.primaryBg, padding: [1, 5], borderRadius: 4,
        },
      })
    for (const o2 of r.owners)
      links.push({
        source: r.id, target: o2.processId,
        symbol: ['none', 'arrow'], symbolSize: 10,
        lineStyle: { color: palette.warning.text, width: 2, type: 'dashed' },
        label: {
          show: true, formatter: o2.mode, fontSize: 11, fontWeight: 'bold', color: palette.warning.text,
          backgroundColor: palette.warning.bg, padding: [1, 5], borderRadius: 4,
        },
      })
  }

  chart.setOption({
    tooltip: {
      confine: true,
      formatter: (p: any) => {
        if (p.dataType !== 'node') return ''
        const raw = p.data.raw
        if (p.data.kind === 'resource')
          return [`<b>${raw.display}</b>`, `类型：${raw.resourceType}`, raw.mode ? `模式：${raw.mode}` : '']
            .filter(Boolean).join('<br/>')
        const lines = [
          `<b>会话 ${raw.spid}</b>${raw.isVictim ? '（牺牲进程）' : ''}`,
          raw.loginName ? `${raw.loginName}@${raw.hostName ?? '-'}${raw.clientApp ? `（${raw.clientApp}）` : ''}` : '',
          raw.isolationLevel ? `隔离级别：${raw.isolationLevel}` : '',
          raw.lockMode ? `申请模式：${raw.lockMode}` : '',
          raw.waitResource ? `等待资源：${raw.waitResource}` : '',
          raw.trancount > 0 ? `事务数：${raw.trancount}（日志 ${raw.logUsed} KB）` : '',
          raw.inputBuf ? `SQL：${raw.inputBuf.slice(0, 90).replace(/\s+/g, ' ')}` : '',
        ].filter(Boolean)
        return lines.join('<br/>')
      },
    },
    animationDuration: 400,
    series: [{
      type: 'graph',
      layout: 'none',
      roam: true,
      zoom: 0.95,   // 环形布局自适应画布，仅略微缩小留边
      data: nodes,
      links,
      emphasis: { focus: 'adjacency', lineStyle: { width: 3 } },
    }],
  }, true)
}

function onResize() {
  chart && !chart.isDisposed() && chart.resize()
}

watch(() => detail.value, async d => {
  if (!d) return
  await nextTick()
  renderGraph()
})

// keep-alive 切回：容器重新挂载，按当前尺寸重排（缓存期间窗口/侧栏变化的兜底）
onActivated(() => onResize())

onMounted(async () => {
  if (!noEvents.value) {
    try {
      detail.value = await getDeadlockDetail(Number(route.params.eventId))
    } finally {
      loading.value = false
    }
  }
  window.addEventListener('resize', onResize)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  chart?.dispose()
  chart = null
})
</script>

<template>
  <div class="page">
    <!-- 引擎守卫：无事件数据源的引擎整页空态不发请求 -->
    <a-empty v-if="noEvents" description="该引擎无死锁事件明细数据源" style="padding: 48px 0" />
    <a-spin v-else :spinning="loading">
      <template v-if="detail">
        <!-- 元信息 -->
        <a-card>
          <template #title>
            <a-space>
              <a-button size="small" @click="router.back()">← 返回</a-button>
              <span>死锁事件 #{{ detail.id }}</span>
            </a-space>
          </template>
          <a-descriptions :column="4" size="small" bordered>
            <a-descriptions-item label="死锁时间">{{ fmtTime(detail.eventTimeUtc) }}</a-descriptions-item>
            <a-descriptions-item label="牺牲进程">
              <a-tag color="red">{{ detail.victimSpids }}</a-tag>
              {{ detail.victimSummary ?? '-' }}
            </a-descriptions-item>
            <a-descriptions-item label="另一进程">{{ detail.otherSummary ?? '-' }}</a-descriptions-item>
            <a-descriptions-item label="指纹">
              <span style="font-family: consolas, monospace">{{ detail.fingerprint }}</span>
            </a-descriptions-item>
          </a-descriptions>
        </a-card>

        <!-- 双方进程辅助信息 -->
        <a-card
          v-for="p in detail.processes"
          :key="p.id"
          :title="`会话 ${p.spid}${p.isVictim ? '（牺牲进程）' : ''}`"
          :class="{ 'victim-card': p.isVictim }"
          style="margin-top: 16px"
        >
          <a-descriptions :column="4" size="small">
            <a-descriptions-item label="登录">{{ p.loginName ?? '-' }}@{{ p.hostName ?? '-' }}</a-descriptions-item>
            <a-descriptions-item label="客户端程序">{{ p.clientApp ?? '-' }}</a-descriptions-item>
            <a-descriptions-item label="隔离级别">{{ p.isolationLevel ?? '-' }}</a-descriptions-item>
            <a-descriptions-item label="申请锁模式">{{ p.lockMode ?? '-' }}</a-descriptions-item>
            <a-descriptions-item label="事务开始">{{ fmtTime(p.lastTranStartedUtc) }}{{ tranDur(p) ? `（${tranDur(p)}）` : '' }}</a-descriptions-item>
            <a-descriptions-item label="最后批次开始">{{ fmtTime(p.lastBatchStartedUtc) }}</a-descriptions-item>
            <a-descriptions-item label="开事务数">{{ p.trancount }}（日志 {{ p.logUsed }} KB）</a-descriptions-item>
            <a-descriptions-item label="等待资源">{{ p.waitResource ?? '-' }}</a-descriptions-item>
          </a-descriptions>

          <div v-if="p.inputBuf" style="margin-top: 8px">
            <div style="font-weight: 600; margin-bottom: 4px">SQL 全文（inputbuf）</div>
            <SqlText :text="p.inputBuf" :max-height="220" />
          </div>
          <div v-if="frameLines(p.executionStack)" style="margin-top: 12px">
            <div style="font-weight: 600; margin-bottom: 4px">执行栈（前 {{ p.executionStack.length }} 帧）</div>
            <pre class="stack-pre">{{ frameLines(p.executionStack)!.join('\n') }}</pre>
          </div>
        </a-card>

        <!-- 死锁关系图（进程节点 + 资源节点 + 申请/持有边，victim 红描边） -->
        <a-card title="死锁关系图" style="margin-top: 16px">
          <div ref="chartEl" style="height: 480px; border: 1px solid var(--border-color-split); border-radius: 8px"></div>
          <!-- 图例：图形 + 文字映射（颜色/形状与图内节点和边一致） -->
          <div class="graph-legend">
            <div class="legend-row">
              <span class="lg-item">
                <svg width="34" height="18" viewBox="0 0 34 18"><rect x="2" y="2" width="30" height="14" rx="3"   stroke-width="2" style="fill:var(--color-primary-bg, #e6f4ff);stroke:var(--color-primary, #1677ff)" /></svg>
                进程
              </span>
              <span class="lg-item">
                <svg width="34" height="18" viewBox="0 0 34 18"><rect x="2" y="2" width="30" height="14" rx="3" fill="rgba(255,77,79,.08)"  stroke-width="2.5" style="stroke:var(--danger-text, #ff4d4f)" /></svg>
                牺牲进程（顶部）
              </span>
              <span class="lg-item">
                <svg width="22" height="20" viewBox="0 0 22 20"><polygon points="11,1 21,10 11,19 1,10" fill="rgba(82,196,26,.08)"  stroke-width="2" style="stroke:var(--success-text, #52c41a)" /></svg>
                键 / 行锁
              </span>
              <span class="lg-item">
                <svg width="26" height="18" viewBox="0 0 26 18"><rect x="2" y="2" width="22" height="14" rx="2" fill="rgba(82,196,26,.08)"  stroke-width="2" style="stroke:var(--success-text, #52c41a)" /></svg>
                页 / 对象锁
              </span>
              <span class="lg-item">
                <svg width="24" height="20" viewBox="0 0 24 20"><polygon points="12,2 22,18 2,18" fill="rgba(82,196,26,.08)"  stroke-width="2" style="stroke:var(--success-text, #52c41a)" /></svg>
                并行交换
              </span>
            </div>
            <div class="legend-row">
              <span class="lg-item">
                <svg width="42" height="14" viewBox="0 0 42 14"><line x1="2" y1="7" x2="32" y2="7"  stroke-width="2" style="stroke:var(--color-primary, #1677ff)" /><polygon points="32,2 42,7 32,12"  style="fill:var(--color-primary, #1677ff)" /></svg>
                申请（进程 → 资源）
              </span>
              <span class="lg-item">
                <svg width="42" height="14" viewBox="0 0 42 14"><line x1="2" y1="7" x2="32" y2="7"  stroke-width="2" stroke-dasharray="5 4" style="stroke:var(--warning-text, #fa8c16)" /><polygon points="32,2 42,7 32,12"  style="fill:var(--warning-text, #fa8c16)" /></svg>
                持有（资源 → 进程）
              </span>
              <span class="lg-item lg-note">环形布局 = 等待环（顺时针）；悬停看详情，滚轮缩放可拖拽</span>
            </div>
          </div>
        </a-card>
      </template>
      <a-empty v-else-if="!loading" description="事件不存在" style="padding: 48px 0" />
    </a-spin>
  </div>
</template>

<style scoped>
/* 关系图图例：图形+文字映射（形状/颜色与 ECharts 节点边一致） */
.graph-legend {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 8px;
}
.legend-row {
  display: flex;
  align-items: center;
  gap: 18px;
  flex-wrap: wrap;
}
.lg-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 12px;
  color: var(--text-secondary);
}
.lg-note {
  color: var(--text-tertiary);
}

/* 牺牲进程卡片标题标红（替代已废弃的 :head-style） */
.victim-card :deep(.ant-card-head-title) {
  color: var(--danger-text);
}
.stack-pre {
  max-height: 160px;
  overflow: auto;
  white-space: pre-wrap;
  background: var(--code-bg);
  padding: 12px;
  font-size: 12px;
  color: var(--text-secondary);
}
</style>
