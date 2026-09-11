<script setup lang="ts">
import { useTextViewer } from '../composables/useTextViewer'

/**
 * 通用截断单元格：超出列宽省略（hover 有原生 title 预览），点击弹全文（全局 TextViewer）。
 * 列宽约定：名称类列 fitColumnWidth 自适应带上限，本组件兜底"超上限可点击看全"。
 */
defineProps<{
  text?: string | null
  /** 全文弹窗标题（列名） */
  viewerTitle?: string
  /** 等宽字体（指纹/对象名/SQL） */
  mono?: boolean
}>()

const { show } = useTextViewer()
</script>

<template>
  <span
    v-if="text"
    class="et"
    :class="{ mono }"
    :title="text"
    @click="show(viewerTitle ?? '全文', text)"
  >{{ text }}</span>
  <span v-else class="et-empty">-</span>
</template>

<style scoped>
.et {
  display: inline-block;
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: bottom;
  cursor: pointer;
}

.et:hover {
  color: var(--color-primary);
}

.et-empty {
  color: var(--text-disabled);
}

.mono {
  font-family: consolas, monospace;
  font-size: 12px;
}
</style>
