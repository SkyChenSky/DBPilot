<script setup lang="ts">
import { useTextViewer } from '../composables/useTextViewer'

const { viewer } = useTextViewer()
</script>

<template>
  <a-modal v-model:open="viewer.open" :title="viewer.title" :width="820" :footer="null">
    <pre class="tv-pre">{{ viewer.text }}</pre>
  </a-modal>
</template>

<style scoped>
.tv-pre {
  max-height: 480px;
  overflow: auto;
  white-space: pre-wrap;
  background: var(--code-bg);
  border: 1px solid var(--border-color-split);
  border-radius: 4px;
  padding: 12px;
  font-family: consolas, monospace;
  font-size: 12px;
}
</style>
