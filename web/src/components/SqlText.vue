<script setup lang="ts">
/**
 * SQL/脚本展示块：code-bg 底 + 左侧 2px 主色竖条 + 等宽字体。
 * 替代各页散落的 .sql-pre 手写样式。
 */
withDefaults(
  defineProps<{
    text?: string | null
    maxHeight?: number
    selectable?: boolean
  }>(),
  { maxHeight: 200, selectable: true },
)
</script>

<template>
  <pre
    class="sql-text"
    :style="{ maxHeight: `${maxHeight}px`, userSelect: selectable ? 'text' : 'none' }"
  >{{ text || '-' }}</pre>
</template>

<style scoped>
.sql-text {
  margin: 0;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
  background: var(--code-bg);
  border: 1px solid var(--border-color-split);
  border-left: 2px solid var(--color-primary);
  border-radius: 4px;
  padding: 12px;
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--text-primary);
}
</style>
