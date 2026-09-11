<script setup lang="ts">
/**
 * 通用统计卡：panel 底 + 标题/数值/单位/语气色/提示。
 * 网格布局（几列、间距）留在页面侧，组件只负责卡片本体。
 */
defineProps<{
  title: string
  /** 数值（null/undefined 显示 '-'） */
  value?: string | number | null
  unit?: string
  /** 语气色：default 主文字色，其余用语义色 */
  tone?: 'default' | 'primary' | 'success' | 'warning' | 'danger'
  /** 标题旁的问号提示（tooltip） */
  hint?: string
  loading?: boolean
}>()
</script>

<template>
  <div class="stat-card">
    <div class="stat-title">
      {{ title }}
      <a-tooltip v-if="hint" :title="hint">
        <span class="stat-hint">?</span>
      </a-tooltip>
    </div>
    <a-spin :spinning="!!loading" size="small" wrapper-class-name="stat-spin">
      <div class="stat-value" :class="tone ?? 'default'">
        {{ value ?? '-' }}<span v-if="unit && value != null" class="stat-unit">{{ unit }}</span>
      </div>
    </a-spin>
  </div>
</template>

<style scoped>
.stat-card {
  background: var(--panel-bg);
  border: 1px solid var(--border-color-split);
  border-radius: 4px;
  padding: 12px 16px;
  min-height: 64px;
}

.stat-title {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-secondary);
  font-size: 12px;
}

.stat-hint {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  border-radius: 50%;
  border: 1px solid var(--border-color);
  color: var(--text-tertiary);
  font-size: 10px;
  line-height: 1;
  cursor: help;
}

.stat-spin {
  display: block;
  margin-top: 4px;
}

.stat-value {
  font-size: 24px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  line-height: 32px;
  color: var(--text-primary);
}

.stat-value.primary {
  color: var(--color-primary);
}

.stat-value.success {
  color: var(--success-text);
}

.stat-value.warning {
  color: var(--warning-text);
}

.stat-value.danger {
  color: var(--danger-text);
}

.stat-unit {
  margin-left: 4px;
  font-size: 12px;
  font-weight: 400;
  color: var(--text-tertiary);
}
</style>
