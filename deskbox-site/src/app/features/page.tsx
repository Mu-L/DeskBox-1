"use client";

import { motion } from "framer-motion";
import Image from "next/image";
import { FluentIcon } from "@/components/FluentIcon";

const features = [
  { title: "收纳格子", subtitle: "把文件收进格子里", description: "创建桌面格子，将文件分门别类。支持图标视图和列表视图，拖拽入格，批量操作。图片文件自动显示缩略图预览。", image: "/widget-light.png", highlights: ["图标视图 / 列表视图切换", "拖拽文件直接入格", "右键菜单批量操作", "图片缩略图预览"] },
  { title: "文件夹映射", subtitle: "不移动文件，原地管理", description: "把已有的文件夹直接映射为桌面格子。保持原有目录结构，不需要复制或移动任何文件。", image: "/widget-dark.png", highlights: ["映射任意文件夹", "保持原有目录结构", "支持拖拽操作", "与收纳格子相同体验"] },
  { title: "随记", subtitle: "剪贴板自动记录", description: "自动记录你复制的文本、链接和截图。随时调用，不用再反复复制。支持置顶收藏和搜索。", image: "/settings-animation.png", highlights: ["自动记录文本、链接、截图", "三个视图：记录 / 置顶 / 最近", "单击复制，双击打开", "支持搜索和保存到格子"] },
  { title: "外观与动画", subtitle: "定制你的桌面风格", description: "明暗主题、透明度、动画效果、圆角样式。丰富的个性化设置，让格子融入你的桌面。", image: "/settings-appearance.png", highlights: ["明暗主题自动切换", "可调节窗口透明度", "多种动画效果和速度", "自定义图标和文字大小"] },
];

const scenarios = [
  { icon: "💼", title: "办公桌整理", desc: "把散落在桌面的文档、表格、快捷方式收进不同格子，需要时一键唤起。" },
  { icon: "📚", title: "学习资料管理", desc: "课程资料、笔记、参考文档按科目分格，不再翻文件夹找文件。" },
  { icon: "📂", title: "项目文件归档", desc: "把已有项目文件夹映射为桌面格子，不移动文件，原地管理。" },
];

export default function FeaturesPage() {
  return (
    <div className="pt-28 pb-20 px-4 sm:px-6 lg:px-8">
      <div className="max-w-6xl mx-auto">
        {/* Header */}
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: [0.16, 1, 0.3, 1] }} className="text-center mb-24">
          <h1 className="text-4xl sm:text-5xl font-bold mb-4">功能</h1>
          <p className="text-[var(--secondary)] text-lg max-w-lg mx-auto">每个功能都围绕一个核心需求设计</p>
        </motion.div>

        {/* Feature Blocks */}
        <div className="space-y-24 mb-24">
          {features.map((feature, index) => (
            <motion.div key={index} initial={{ opacity: 0, y: 50 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true, margin: "-80px" }} transition={{ duration: 0.7, ease: [0.16, 1, 0.3, 1] }}
              className={`flex flex-col ${index % 2 === 1 ? "lg:flex-row-reverse" : "lg:flex-row"} gap-16 items-center`}>
              <div className="lg:w-2/5">
                <span className="inline-block text-xs px-2.5 py-1 rounded-full font-medium bg-[var(--accent-light)] text-[var(--accent)] mb-3">{feature.subtitle}</span>
                <h2 className="text-3xl sm:text-4xl font-bold mb-4">{feature.title}</h2>
                <p className="text-[var(--secondary)] text-lg leading-relaxed mb-6">{feature.description}</p>
                <ul className="space-y-3">
                  {feature.highlights.map((h, i) => (
                    <li key={i} className="flex items-center gap-3">
                      <FluentIcon name="checkmark" size={16} className="text-emerald-500 flex-shrink-0" />
                      <span className="text-[var(--secondary)]">{h}</span>
                    </li>
                  ))}
                </ul>
              </div>
              <div className="lg:w-3/5">
                <div className="feature-image-wrapper rounded-xl border border-[var(--card-border)] shadow-xl">
                  <div className="absolute inset-0 bg-gradient-to-br from-[var(--accent)]/10 to-transparent rounded-2xl blur-3xl" style={{ position: "absolute", zIndex: 0 }} />
                  <Image src={feature.image} alt={feature.title} width={900} height={560} className="relative rounded-xl w-full" style={{ position: "relative", zIndex: 1 }} />
                </div>
              </div>
            </motion.div>
          ))}
        </div>

        {/* Drag Diagnostics */}
        <motion.div initial={{ opacity: 0, y: 30 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.6 }} className="fluent-card mb-32">
          <div className="flex items-start gap-4">
            <div className="w-12 h-12 rounded-xl bg-[var(--accent-light)] flex items-center justify-center text-2xl flex-shrink-0">🔧</div>
            <div>
              <h2 className="text-2xl font-bold mb-3">拖拽诊断</h2>
              <p className="text-[var(--secondary)] leading-relaxed mb-4">Windows 10/11 存在已知的拖拽兼容性问题，DeskBox 内置了一键诊断和修复工具，帮你解决拖拽不响应、文件丢失等问题。</p>
              <div className="flex flex-wrap gap-2">
                {["一键诊断", "自动修复", "兼容 Win10/11", "无需重启"].map((tag) => (
                  <span key={tag} className="text-xs px-3 py-1 rounded-full bg-[var(--background)] text-[var(--secondary)]">{tag}</span>
                ))}
              </div>
            </div>
          </div>
        </motion.div>

        {/* Scenarios */}
        <div>
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3">用在这些场景</h2>
            <p className="text-[var(--secondary)] text-lg">不是替代桌面，是帮桌面多一层整理能力</p>
          </motion.div>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
            {scenarios.map((s, i) => (
              <motion.div key={i} initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5, delay: i * 0.1 }} className="fluent-card">
                <div className="text-2xl mb-3">{s.icon}</div>
                <h3 className="text-lg font-semibold mb-2">{s.title}</h3>
                <p className="text-[var(--secondary)] text-sm leading-relaxed">{s.desc}</p>
              </motion.div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
