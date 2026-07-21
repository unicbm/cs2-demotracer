import { createPortal } from "react-dom";
import { type ReactNode, useEffect, useLayoutEffect, useRef, useState } from "react";
import "./context-menu.css";

export interface ContextMenuItem {
  label: string;
  icon?: ReactNode;
  disabled?: boolean;
  dividerBefore?: boolean;
  onSelect: () => void;
}

export interface ContextMenuState {
  x: number;
  y: number;
  items: ContextMenuItem[];
  label: string;
}

export function ContextMenu({ menu, onClose }: {
  menu: ContextMenuState;
  onClose: () => void;
}) {
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [position, setPosition] = useState({ left: menu.x, top: menu.y });

  useLayoutEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const margin = 8;
    const bounds = root.getBoundingClientRect();
    setPosition({
      left: Math.max(margin, Math.min(menu.x, window.innerWidth - bounds.width - margin)),
      top: Math.max(margin, Math.min(menu.y, window.innerHeight - bounds.height - margin)),
    });
    root.querySelector<HTMLButtonElement>("button:not(:disabled)")?.focus({ preventScroll: true });
  }, [menu]);

  useEffect(() => {
    const close = (event: Event) => {
      if (event.target instanceof Node && rootRef.current?.contains(event.target)) return;
      onClose();
    };
    const keydown = (event: KeyboardEvent) => {
      const root = rootRef.current;
      if (!root) return;
      if (event.key === "Escape") {
        event.preventDefault();
        onClose();
        return;
      }
      if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
      const items = [...root.querySelectorAll<HTMLButtonElement>("button:not(:disabled)")];
      if (items.length === 0) return;
      event.preventDefault();
      const current = Math.max(0, items.indexOf(document.activeElement as HTMLButtonElement));
      const next = event.key === "Home"
        ? 0
        : event.key === "End"
          ? items.length - 1
          : event.key === "ArrowDown"
            ? (current + 1) % items.length
            : (current - 1 + items.length) % items.length;
      items[next]?.focus();
    };
    window.addEventListener("pointerdown", close, true);
    window.addEventListener("blur", onClose);
    window.addEventListener("resize", onClose);
    window.addEventListener("scroll", onClose, true);
    window.addEventListener("keydown", keydown);
    return () => {
      window.removeEventListener("pointerdown", close, true);
      window.removeEventListener("blur", onClose);
      window.removeEventListener("resize", onClose);
      window.removeEventListener("scroll", onClose, true);
      window.removeEventListener("keydown", keydown);
    };
  }, [onClose]);

  return createPortal(
    <div
      ref={rootRef}
      className="context-menu"
      role="menu"
      aria-label={menu.label}
      style={{ left: position.left, top: position.top }}
      onContextMenu={(event) => event.preventDefault()}
    >
      {menu.items.map((item, index) => (
        <div className={item.dividerBefore ? "has-divider" : ""} key={`${item.label}-${index}`}>
          <button
            type="button"
            role="menuitem"
            disabled={item.disabled}
            onClick={() => {
              onClose();
              item.onSelect();
            }}
          >
            <span aria-hidden="true">{item.icon}</span>
            <strong>{item.label}</strong>
          </button>
        </div>
      ))}
    </div>,
    document.body,
  );
}
