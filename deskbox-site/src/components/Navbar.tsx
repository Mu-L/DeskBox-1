"use client";

import Image from "next/image";
import Link from "next/link";
import { useState, useEffect, useRef } from "react";
import { usePathname } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";
import { FluentIcon } from "@/components/FluentIcon";

const navItems = [
  { href: "/", label: "首页" },
  { href: "/features", label: "功能" },
  { href: "/download", label: "下载" },
  { href: "/roadmap", label: "路线图" },
  { href: "/changelog", label: "更新日志" },
  { href: "/about", label: "关于" },
];

function ThemeToggle() {
  const [theme, setTheme] = useState<"light" | "dark">("light");
  const [mounted, setMounted] = useState(false);

  useEffect(() => {
    setMounted(true);
    const saved = localStorage.getItem("deskbox_theme") as "light" | "dark" | null;
    if (saved) {
      setTheme(saved);
      document.documentElement.setAttribute("data-theme", saved);
    } else {
      const prefersDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
      setTheme(prefersDark ? "dark" : "light");
      document.documentElement.setAttribute("data-theme", prefersDark ? "dark" : "light");
    }
  }, []);

  const toggle = () => {
    const next = theme === "light" ? "dark" : "light";
    setTheme(next);
    localStorage.setItem("deskbox_theme", next);
    document.documentElement.setAttribute("data-theme", next);
  };

  if (!mounted) return <div className="w-9 h-9" />;

  return (
    <button
      onClick={toggle}
      className="w-9 h-9 rounded-lg flex items-center justify-center text-[var(--secondary)] hover:text-[var(--foreground)] hover:bg-[var(--card-border)]/50 transition-all duration-150"
      aria-label={theme === "light" ? "切换到深色模式" : "切换到浅色模式"}
    >
      <AnimatePresence mode="wait" initial={false}>
        {theme === "light" ? (
          <motion.div
            key="sun"
            initial={{ rotate: -90, opacity: 0 }}
            animate={{ rotate: 0, opacity: 1 }}
            exit={{ rotate: 90, opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <FluentIcon name="sun" size={18} />
          </motion.div>
        ) : (
          <motion.div
            key="moon"
            initial={{ rotate: 90, opacity: 0 }}
            animate={{ rotate: 0, opacity: 1 }}
            exit={{ rotate: -90, opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <FluentIcon name="moon" size={18} />
          </motion.div>
        )}
      </AnimatePresence>
    </button>
  );
}

export function Navbar() {
  const [isOpen, setIsOpen] = useState(false);
  const [visible, setVisible] = useState(true);
  const pathname = usePathname();
  const lastScrollY = useRef(0);

  useEffect(() => {
    const onScroll = () => {
      const y = window.scrollY;
      if (y < 10) {
        setVisible(true);
      } else if (y > lastScrollY.current + 5) {
        setVisible(false);
        setIsOpen(false);
      } else if (y < lastScrollY.current - 5) {
        setVisible(true);
      }
      lastScrollY.current = y;
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  const isActive = (href: string) => {
    if (href === "/") return pathname === "/";
    return pathname.startsWith(href);
  };

  return (
    <motion.nav
      initial={false}
      animate={{ y: visible ? 0 : -80 }}
      transition={{ duration: 0.25, ease: [0.16, 1, 0.3, 1] }}
      className="fixed top-0 left-0 right-0 z-50 backdrop-blur-xl bg-[var(--background)]/80 border-b border-[var(--card-border)]"
      style={{ willChange: "transform" }}
    >
      <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between h-16">
          <Link href="/" className="flex items-center gap-2.5">
            <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={28} height={28} />
            <span className="font-semibold text-lg tracking-tight">DeskBox</span>
          </Link>
          <div className="hidden md:flex items-center gap-1">
            {navItems.map((item) => (
              <Link
                key={item.href}
                href={item.href}
                className={`relative px-3 py-2 rounded-lg text-sm font-medium transition-all duration-150 ${
                  isActive(item.href)
                    ? "text-[var(--foreground)]"
                    : "text-[var(--secondary)] hover:text-[var(--foreground)] hover:bg-[var(--card-border)]/50"
                }`}
              >
                {item.label}
                {isActive(item.href) && (
                  <motion.span
                    layoutId="nav-indicator"
                    className="absolute bottom-0 left-1/2 -translate-x-1/2 h-[3px] w-5 rounded-full bg-[var(--accent)]"
                    transition={{ type: "spring", stiffness: 400, damping: 30 }}
                  />
                )}
              </Link>
            ))}
            <div className="ml-2 flex items-center gap-1">
              <ThemeToggle />
            </div>
          </div>
          <div className="flex md:hidden items-center gap-1">
            <ThemeToggle />
            <button className="p-2 rounded-lg hover:bg-[var(--card-border)] transition-colors" onClick={() => setIsOpen(!isOpen)} aria-label="Toggle menu">
              <FluentIcon name={isOpen ? "dismiss" : "line-horizontal-3"} size={24} />
            </button>
          </div>
        </div>
      </div>
      <AnimatePresence>
        {isOpen && (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.2 }} className="md:hidden border-t border-[var(--card-border)] overflow-hidden">
            <div className="px-4 py-4 space-y-2">
              {navItems.map((item) => (
                <Link
                  key={item.href}
                  href={item.href}
                  className={`block py-3 px-4 rounded-lg transition-all ${
                    isActive(item.href)
                      ? "text-[var(--accent)] bg-[var(--accent-light)] font-medium"
                      : "text-[var(--secondary)] hover:text-[var(--foreground)] hover:bg-[var(--card-border)]"
                  }`}
                  onClick={() => setIsOpen(false)}
                >
                  {item.label}
                </Link>
              ))}
              <Link href="/download" className="block fluent-button text-center mt-4" onClick={() => setIsOpen(false)}>下载</Link>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.nav>
  );
}
