import { defineStore } from 'pinia'
import { listInstances, type InstanceItem } from '../api/instance'

const STORAGE_KEY = 'dbpilot:instance-id'

/** 顶栏首载去重：并发调用（布局 + 多页同时 ensureLoaded）只发一次请求；失败置空允许重试 */
let ensurePromise: Promise<void> | null = null

/**
 * 全局实例上下文：顶栏选一次，所有诊断页共用（方案 A，对齐阿里云 DAS）。
 * - instanceId 持久化 localStorage（刷新/重登保持；实例已删则回落第一个）；
 * - 页面不直接选实例，经 useInstanceDatabases / useGlobalInstance 接线消费。
 */
export const useInstanceContext = defineStore('instanceContext', {
  state: () => ({
    instances: [] as InstanceItem[],
    instanceId: undefined as number | undefined,
    loaded: false,
  }),
  getters: {
    /** 当前实例对象（未加载/无实例时 undefined） */
    current(state): InstanceItem | undefined {
      return state.instances.find(i => i.id === state.instanceId)
    },
    /** 当前实例引擎（sqlserver / mysql；B8 起页面按此隐藏不支持入口） */
    currentEngine(): string | undefined {
      return this.current?.engine
    },
  },
  actions: {
    /** 首载：拉实例列表并恢复持久化选择（并发去重，重复调用直接复用同一 Promise） */
    ensureLoaded(): Promise<void> {
      ensurePromise ??= (async () => {
        const page = await listInstances({ page: 1, limit: 100 })
        this.instances = page.items
        const saved = Number(localStorage.getItem(STORAGE_KEY))
        this.instanceId = this.instances.some(i => i.id === saved) ? saved : this.instances[0]?.id
        this.loaded = true
      })().catch((e) => {
        ensurePromise = null
        throw e
      })
      return ensurePromise
    },
    /** 顶栏/实例管理页「诊断」切换全局实例（持久化） */
    setInstance(id: number) {
      this.instanceId = id
      localStorage.setItem(STORAGE_KEY, String(id))
    },
  },
})
