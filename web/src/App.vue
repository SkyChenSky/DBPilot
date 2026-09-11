<script setup lang="ts">
import TextViewer from './components/TextViewer.vue'
import { workbenchTheme } from './theme/antdTheme'
</script>

<template>
  <!-- 主题总入口：浅色工作台（全局）；暗色外壳区块各自嵌套 chromeTheme；a-app 让 message 等静态 API 也吃主题 -->
  <a-config-provider :theme="workbenchTheme">
    <a-app>
      <router-view />
      <!-- 全站"点击查看全文"弹窗宿主（EllipsisText 触发） -->
      <TextViewer />
    </a-app>
  </a-config-provider>
</template>
