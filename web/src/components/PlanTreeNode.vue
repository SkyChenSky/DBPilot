<script setup lang="ts">
import { ref } from 'vue'
import type { PlanTreeNode as Node } from '../api/topsql'
import { fmtNum } from '../utils/format'

const props = defineProps<{ node: Node; depth?: number }>()

/** 默认展开前 2 层（深树兜底），可点击标题行折叠 */
const expanded = ref((props.depth ?? 0) < 2)

/** 物理操作配色：Scan 系标红、Seek 系标绿、Sort/Spill 风险见黄标 */
function opColor(op: string) {
  if (op.includes('Scan')) return 'red'
  if (op.includes('Seek')) return 'green'
  if (op === 'Table Spool' || op === 'Row Count Spool') return 'orange'
  return 'blue'
}
</script>

<template>
  <div class="plan-node">
    <div class="card" @click="expanded = !expanded">
      <div class="row1">
        <span class="toggle">{{ node.children.length ? (expanded ? '▾' : '▸') : '·' }}</span>
        <a-tag :color="opColor(node.physicalOp)" class="op">{{ node.physicalOp }}</a-tag>
        <span class="logical">{{ node.logicalOp }}</span>
        <a-tag v-if="node.parallel" color="gold">并行</a-tag>
        <a-tag v-if="node.spillWarning" color="gold">Spill 溢出</a-tag>
      </div>
      <div class="row2">
        <span>子树开销 {{ fmtNum(node.subtreeCost, 2) }}</span>
        <span>估计行 {{ fmtNum(node.estimateRows, 2) }}</span>
        <span>执行次数 {{ fmtNum(node.estimateExecutions, 2) }}</span>
        <span v-if="node.objectName" class="obj" :title="node.objectName">{{ node.objectName }}</span>
      </div>
      <div v-if="node.predicate" class="pred" :title="node.predicate">谓词：{{ node.predicate }}</div>
      <div v-if="node.seekPredicates" class="pred seek" :title="node.seekPredicates">Seek：{{ node.seekPredicates.slice(0, 160) }}</div>
    </div>
    <div v-if="expanded && node.children.length" class="children">
      <PlanTreeNode v-for="(c, i) in node.children" :key="i" :node="c" :depth="(depth ?? 0) + 1" />
    </div>
  </div>
</template>

<script lang="ts">
export default { name: 'PlanTreeNode' }
</script>

<style scoped>
.plan-node {
  margin-bottom: 4px;
}
.card {
  padding: 6px 10px;
  border: 1px solid var(--border-color-split);
  border-radius: 6px;
  background: var(--panel-bg);
  cursor: pointer;
}
.card:hover {
  border-color: var(--border-color);
}
.row1 {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.toggle {
  color: var(--text-tertiary);
  width: 12px;
}
.op {
  margin-inline-end: 0;
  font-weight: 600;
}
.logical {
  color: var(--text-secondary);
  font-size: 12px;
}
.row2 {
  margin-top: 4px;
  display: flex;
  align-items: center;
  gap: 14px;
  flex-wrap: wrap;
  color: var(--text-secondary);
  font-size: 12px;
}
.obj {
  font-family: consolas, monospace;
  color: var(--color-primary);
}
.pred {
  margin-top: 4px;
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--text-tertiary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.seek {
  color: var(--success-text);
}
.children {
  margin-left: 24px;
  padding-left: 14px;
  border-left: 2px dashed var(--border-color);
  margin-top: 6px;
}
</style>
