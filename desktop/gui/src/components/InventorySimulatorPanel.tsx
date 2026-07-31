/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useRef, type CSSProperties, type RefObject } from "react";
import { ArrowIcon } from "../icons";
import type { Language } from "../types";

interface InventorySimulatorPanelProps {
  available: boolean;
  hostRef: RefObject<HTMLDivElement | null>;
  language: Language;
  open: boolean;
  resizing: boolean;
  width: number;
  onCollapse: () => void;
  onExpand: () => void;
  onResizeEnd: () => void;
  onResizeStart: () => void;
  onWidthChange: (width: number) => void;
  onWidthReset: () => void;
}

export function InventorySimulatorPanel({
  available,
  hostRef,
  language,
  open,
  resizing,
  width,
  onCollapse,
  onExpand,
  onResizeEnd,
  onResizeStart,
  onWidthChange,
  onWidthReset,
}: InventorySimulatorPanelProps) {
  const dragRef = useRef<{ pointerId: number; startWidth: number; startX: number } | null>(null);

  if (!open) {
    if (!available) return null;
    return (
      <button
        className="inventory-simulator-rail"
        type="button"
        onClick={onExpand}
        title={language === "zh" ? "展开 Inventory Simulator" : "Expand Inventory Simulator"}
      >
        <ArrowIcon size={15} />
        <span>{language === "zh" ? "库存" : "Inventory"}</span>
      </button>
    );
  }

  return (
    <aside
      className={`inventory-simulator-panel${resizing ? " is-resizing" : ""}`}
      aria-label="Inventory Simulator"
      style={{ "--inventory-simulator-panel-width": `${width}px` } as CSSProperties}
    >
      <div
        className="inventory-simulator-splitter"
        role="separator"
        aria-label={language === "zh" ? "调整 Inventory Simulator 宽度" : "Resize Inventory Simulator"}
        aria-orientation="vertical"
        aria-valuemin={440}
        aria-valuemax={900}
        aria-valuenow={Math.round(width)}
        tabIndex={0}
        onDoubleClick={onWidthReset}
        onKeyDown={(event) => {
          if (event.key === "ArrowLeft") {
            event.preventDefault();
            onWidthChange(width + (event.shiftKey ? 64 : 16));
          } else if (event.key === "ArrowRight") {
            event.preventDefault();
            onWidthChange(width - (event.shiftKey ? 64 : 16));
          } else if (event.key === "Home") {
            event.preventDefault();
            onWidthChange(900);
          } else if (event.key === "End") {
            event.preventDefault();
            onWidthChange(440);
          }
        }}
        onPointerDown={(event) => {
          if (event.button !== 0) return;
          dragRef.current = {
            pointerId: event.pointerId,
            startWidth: event.currentTarget.parentElement?.getBoundingClientRect().width ?? width,
            startX: event.clientX,
          };
          event.currentTarget.setPointerCapture(event.pointerId);
          onResizeStart();
          event.preventDefault();
        }}
        onPointerMove={(event) => {
          const drag = dragRef.current;
          if (!drag || drag.pointerId !== event.pointerId) return;
          onWidthChange(drag.startWidth + drag.startX - event.clientX);
        }}
        onPointerUp={(event) => {
          if (dragRef.current?.pointerId !== event.pointerId) return;
          dragRef.current = null;
          event.currentTarget.releasePointerCapture(event.pointerId);
          onResizeEnd();
        }}
        onPointerCancel={(event) => {
          if (dragRef.current?.pointerId !== event.pointerId) return;
          dragRef.current = null;
          onResizeEnd();
        }}
      />
      <div className="inventory-simulator-panel-surface">
        <header className="inventory-simulator-panel-header">
          <div className="inventory-simulator-panel-title">
            <strong>Inventory Simulator</strong>
            <span>inventory.cstrike.app</span>
          </div>
          <button
            className="inventory-simulator-panel-toggle"
            type="button"
            onClick={onCollapse}
            aria-label={language === "zh" ? "收起 Inventory Simulator" : "Collapse Inventory Simulator"}
            title={language === "zh" ? "收起到右侧" : "Collapse to the right"}
          >
            <span>{language === "zh" ? "收起" : "Collapse"}</span>
            <ArrowIcon size={15} />
          </button>
        </header>
        <div className="inventory-simulator-webview-host" ref={hostRef}>
          <div className="inventory-simulator-placeholder" aria-hidden="true">
            <i />
            <span>{resizing
              ? (language === "zh" ? "正在调整布局…" : "Adjusting layout…")
              : (language === "zh" ? "正在载入官网…" : "Loading official site…")}</span>
          </div>
        </div>
      </div>
    </aside>
  );
}
