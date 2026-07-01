"use client";

import Link from "next/link";
import { motion } from "framer-motion";

export default function NotFound() {
  return (
    <div className="min-h-screen flex items-center justify-center px-4">
      <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} className="text-center max-w-md">
        <div className="text-8xl font-bold text-[var(--accent)] mb-6">404</div>
        <h1 className="text-2xl font-bold mb-3">页面不存在</h1>
        <p className="text-[var(--secondary)] mb-8">你访问的页面可能已被移动或删除</p>
        <div className="flex flex-wrap justify-center gap-3">
          <Link href="/" className="fluent-button">返回首页</Link>
          <Link href="/download" className="fluent-button-secondary">下载 DeskBox</Link>
        </div>
      </motion.div>
    </div>
  );
}
