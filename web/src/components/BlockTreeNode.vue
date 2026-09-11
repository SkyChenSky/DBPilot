<script setup lang="ts">
import { computed } from 'vue'
import type { BlockingNode } from '../api/blocking'
import { fmtMsUnit } from '../utils/format'

const props = defineProps<{ node: BlockingNode; root?: boolean }>()

const emit = defineEmits<{ (e: 'showSql', node: BlockingNode): void }>()

function who(n: BlockingNode) {
  const parts = [n.loginName, n.hostName].filter(Boolean).join('@')
  return parts || '-'
}

/** 锁按对象聚合（同一表的 OBJECT IX + KEY X 合成一行），等待/持有时分别列出并去重 */
const lockGroups = computed(() => {
  const map = new Map<string, { name: string; waits: string[]; holds: string[] }>()
  for (const l of props.node.locks ?? []) {
    const name = l.objectName ?? `${l.dbName ?? ''}（${l.resourceType}）`
    const g = map.get(name) ?? { name, waits: [], holds: [] }
    const desc = `${l.lockMode}`
    const list = l.lockStatus === 'WAIT' ? g.waits : g.holds
    if (!list.includes(desc)) list.push(desc)
    map.set(name, g)
  }
  return [...map.values()].sort((a, b) => b.waits.length - a.waits.length)
})
</script>

<template>
  <div class="node-wrap">
    <div class="node" :class="{ 'root-blocker': root, system: node.isSystem }">
      <div class="row1">
        <span class="sid">会话 {{ node.sessionId }}</span>
        <a-tag v-if="root && !node.isSystem" color="red">根阻塞</a-tag>
        <a-tag v-if="node.isSystem" color="default">系统节点（{{ node.status }}，不可 Kill）</a-tag>
        <a-tag v-else-if="node.isSleepingHead" color="orange">睡着拿锁</a-tag>
        <span class="who">{{ who(node) }}<template v-if="node.programName">（{{ node.programName }}）</template></span>
        <span v-if="node.dbName" class="db">{{ node.dbName }}</span>
      </div>
      <div class="row2">
        <template v-if="node.isSystem">
          无会话实体 —— 分布式事务孤儿 / 延迟恢复占位
        </template>
        <template v-else>
          <template v-if="node.waitType">等待 <b>{{ node.waitType }}</b> {{ fmtMsUnit(node.waitTimeMs) }}<template v-if="node.waitResource && !lockGroups.length"> ｜ 资源 {{ node.waitResource }}</template></template>
          <template v-if="node.totalElapsedMs != null"> ｜ 已执行 {{ fmtMsUnit(node.totalElapsedMs) }}</template>
          <template v-if="node.openTranCount > 0"> ｜ 开事务 {{ node.openTranCount }}</template>
          <template v-if="node.status"> ｜ {{ node.status }}</template>
        </template>
      </div>
      <div v-if="lockGroups.length" class="locks">
        <div v-for="g in lockGroups.slice(0, 3)" :key="g.name" class="lock">
          <span class="lock-obj">{{ g.name }}</span>
          <span v-if="g.waits.length" class="lock-wait">等待 {{ g.waits.join('、') }}</span>
          <span v-if="g.holds.length" class="lock-hold">持有 {{ g.holds.join('、') }}</span>
        </div>
        <span v-if="lockGroups.length > 3" class="lock-more">另有 {{ lockGroups.length - 3 }} 个对象…</span>
      </div>
      <div v-if="node.sqlText" class="sql" :title="node.sqlText" @click="emit('showSql', node)">
        {{ node.sqlText.slice(0, 120).replace(/\s+/g, ' ') }}
      </div>
    </div>
    <div v-if="node.children.length" class="children">
      <BlockTreeNode
        v-for="c in node.children"
        :key="c.sessionId"
        :node="c"
        @showSql="(n: BlockingNode) => emit('showSql', n)"
      />
    </div>
  </div>
</template>

<script lang="ts">
export default { name: 'BlockTreeNode' }
</script>

<style scoped>
.node-wrap {
  margin-bottom: 4px;
}
.node {
  padding: 8px 12px;
  border: 1px solid var(--border-color-split);
  border-radius: 6px;
  background: var(--panel-bg);
}
.root-blocker {
  border-color: var(--danger-border);
  background: var(--danger-bg);
}
.system {
  border-style: dashed;
  background: var(--code-bg);
}
.row1 {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.sid {
  font-weight: 600;
}
.who {
  color: var(--text-secondary);
}
.db {
  color: var(--text-tertiary);
}
.row2 {
  margin-top: 4px;
  color: var(--text-secondary);
  font-size: 12px;
}
.locks {
  margin-top: 6px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.lock {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  flex-wrap: wrap;
}
.lock-obj {
  font-weight: 600;
  color: var(--text-primary);
}
.lock-wait {
  color: var(--danger-text);
  background: var(--danger-bg);
  padding: 0 6px;
  border-radius: 4px;
}
.lock-hold {
  color: var(--warning-text);
  background: var(--warning-bg);
  padding: 0 6px;
  border-radius: 4px;
}
.lock-more {
  font-size: 12px;
  color: var(--text-tertiary);
}
.sql {
  margin-top: 6px;
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--color-primary);
  background: var(--code-bg);
  border-radius: 4px;
  padding: 4px 8px;
  cursor: pointer;
  word-break: break-all;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.children {
  margin-left: 28px;
  padding-left: 16px;
  border-left: 2px dashed var(--border-color);
  margin-top: 6px;
}
</style>
