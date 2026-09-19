<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { listInstances, type InstanceItem } from '../api/instance'

/**
 * 通用实例选择器：选项口径「名称（主机）」全站统一，支持按名称/主机搜索。
 * - 页面已有 useInstanceDatabases 时传 :instances 复用同一份列表（不重复请求）；
 * - 独立使用（无 composable）时不传，组件内部自加载。
 */
const props = withDefaults(
  defineProps<{
    value?: number
    /** 外部实例列表（useInstanceDatabases().instances）；不传则内部加载 */
    instances?: InstanceItem[]
    width?: number
    placeholder?: string
  }>(),
  { width: 260, placeholder: '选择实例' },
)

const emit = defineEmits<{
  (e: 'update:value', v: number | undefined): void
  (e: 'change', v: number | undefined): void
}>()

const inner = ref<InstanceItem[]>([])
const loading = ref(false)

const options = computed(() =>
  (props.instances ?? inner.value).map(i => ({ value: i.id, label: `${i.name}（${i.host}）` })),
)

function onChange(v: number | undefined) {
  emit('update:value', v)
  emit('change', v)
}

onMounted(async () => {
  if (props.instances) return
  loading.value = true
  try {
    const page = await listInstances({ page: 1, limit: 100 })
    inner.value = page.items
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <a-select
    :value="value"
    :style="{ width: `${width}px` }"
    :placeholder="placeholder"
    :loading="loading"
    :options="options"
    show-search
    option-filter-prop="label"
    @change="onChange"
  />
</template>
