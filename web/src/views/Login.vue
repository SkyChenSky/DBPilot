<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { useAuthStore } from '../stores/auth'
import { chromeTheme } from '../theme/antdTheme'
import BrandMark from '../components/BrandMark.vue'

const router = useRouter()
const auth = useAuthStore()

const form = reactive({ username: '', password: '' })
const loading = ref(false)

async function onSubmit() {
  if (!form.username || !form.password) {
    message.warning('请输入用户名和密码')
    return
  }
  loading.value = true
  try {
    await auth.login(form.username, form.password)
    message.success('登录成功')
    router.push('/')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <!-- 登录页属暗色外壳：嵌套暗色 provider（全局是浅色工作台主题），输入框才不会变白 -->
  <a-config-provider :theme="chromeTheme">
    <div class="login-page">
      <div class="login-box">
      <div class="brand">
        <BrandMark :size="48" />
        <div class="brand-name"><span class="db">DB</span><span class="pilot">Pilot</span></div>
      </div>
      <div class="subtitle">数据库自治诊断平台</div>
      <a-card class="login-card" :bordered="false">
        <a-form layout="vertical" @finish="onSubmit">
          <a-form-item label="用户名">
            <a-input v-model:value="form.username" placeholder="用户名" @press-enter="onSubmit" />
          </a-form-item>
          <a-form-item label="密码">
            <a-input-password
              v-model:value="form.password"
              placeholder="密码"
              @press-enter="onSubmit"
            />
          </a-form-item>
          <a-button type="primary" block :loading="loading" html-type="submit" @click="onSubmit">
            登 录
          </a-button>
        </a-form>
      </a-card>
      <div class="features">性能洞察 · 锁诊断 · 慢日志</div>
      </div>
    </div>
  </a-config-provider>
</template>

<style scoped>
.login-page {
  min-height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  /* 外壳深底 + Grafana 式网格线 + 左上蓝/右下青光晕 */
  background-color: var(--chrome-bg-deep);
  background-image:
    radial-gradient(600px circle at 15% 10%, rgba(60, 158, 255, 0.08), transparent 60%),
    radial-gradient(700px circle at 85% 90%, rgba(54, 207, 201, 0.06), transparent 60%),
    repeating-linear-gradient(0deg, rgba(255, 255, 255, 0.015) 0 1px, transparent 1px 48px),
    repeating-linear-gradient(90deg, rgba(255, 255, 255, 0.015) 0 1px, transparent 1px 48px);
}

.login-box {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 32px 16px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;
}

.brand-name {
  font-size: 28px;
  font-weight: 700;
  letter-spacing: 1px;
  line-height: 1;
}

.brand-name .db {
  color: var(--chrome-primary);
}

.brand-name .pilot {
  color: var(--chrome-text-primary);
}

.subtitle {
  margin: 10px 0 28px;
  color: var(--chrome-text-secondary);
  font-size: 14px;
  letter-spacing: 2px;
}

/* 玻璃拟态登录卡：panel 底半透明 + 毛玻璃 */
.login-card {
  width: 380px;
  border-radius: 8px;
  border: 1px solid var(--chrome-border);
  background: rgba(21, 27, 38, 0.75);
  backdrop-filter: blur(12px);
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

/* 登录页输入框的自动填充保持暗色（全局 autofill 规则是浅色工作台口径） */
.login-card :deep(input:-webkit-autofill),
.login-card :deep(input:-webkit-autofill:hover),
.login-card :deep(input:-webkit-autofill:focus) {
  -webkit-text-fill-color: var(--chrome-text-primary);
  caret-color: var(--chrome-text-primary);
  box-shadow: 0 0 0 1000px var(--chrome-panel) inset;
  transition: background-color 9999s ease-in-out 0s;
}

.features {
  margin-top: 20px;
  color: var(--chrome-text-tertiary);
  font-size: 12px;
  letter-spacing: 1px;
}
</style>
