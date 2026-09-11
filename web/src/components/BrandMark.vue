<script setup lang="ts">
/** 纯 CSS 品牌标：渐变方块 + 终端提示符 >_（无图标库依赖） */
withDefaults(defineProps<{ size?: number }>(), { size: 24 })
</script>

<template>
  <div class="brand-mark" :style="{ width: `${size}px`, height: `${size}px` }">
    <span class="brand-mark-prompt" :style="{ fontSize: `${Math.round(size * 0.4)}px` }">&gt;_</span>
  </div>
</template>

<style scoped>
.brand-mark {
  border-radius: 22%;
  background: linear-gradient(135deg, var(--color-primary), var(--color-cyan));
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  box-shadow: 0 2px 8px rgba(60, 158, 255, 0.25);
}
.brand-mark-prompt {
  font-family: Consolas, Monaco, 'Courier New', monospace;
  font-weight: 700;
  color: #0b0e14;
  line-height: 1;
  letter-spacing: -0.5px;
  user-select: none;
}
</style>
