import { reactive } from 'vue'

/** 全站"点击查看全文"弹窗状态（模块级单例，宿主 TextViewer 挂在 App.vue，EllipsisText 调 show） */
const viewer = reactive({ open: false, title: '', text: '' })

export function useTextViewer() {
  return {
    viewer,
    show(title: string, text: string) {
      viewer.title = title
      viewer.text = text
      viewer.open = true
    },
  }
}
