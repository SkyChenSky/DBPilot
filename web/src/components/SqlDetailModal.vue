<script setup lang="ts">
import SqlText from './SqlText.vue'

/** SQL 全文弹窗（TopSql 详情 / 慢SQL 全文与样例等同构弹窗收敛）：
 *  title + 可选 meta 插槽（元信息行/描述列表）+ SQL 正文；双段/带图等异构弹窗仍页面自建。 */
defineProps<{
  open: boolean
  title: string
  sql: string
  maxHeight?: number
}>()

const emit = defineEmits<{ (e: 'update:open', v: boolean): void }>()
</script>

<template>
  <a-modal :open="open" :title="title" :width="800" :footer="null" @update:open="emit('update:open', $event)">
    <div v-if="$slots.meta" class="detail-meta"><slot name="meta" /></div>
    <SqlText :text="sql" :max-height="maxHeight ?? 420" />
    <slot />
  </a-modal>
</template>

<style scoped>
.detail-meta {
  margin-bottom: 8px;
  color: var(--text-tertiary);
  font-size: 12px;
}
</style>
