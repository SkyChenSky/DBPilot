<script setup lang="ts">
defineProps<{
  title: string
  description?: string
}>()
</script>

<template>
  <div class="page-placeholder">
    <h2 class="title">{{ title }}</h2>
    <a-empty :description="description ?? '功能开发中，敬请期待'" />
  </div>
</template>

<style scoped>
.page-placeholder {
  padding: 48px 0;
  text-align: center;
}

.title {
  margin-bottom: 24px;
  font-weight: 500;
  color: var(--text-secondary);
}
</style>
