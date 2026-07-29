import {
  type MouseEvent,
  type ReactNode,
  type RefObject,
  useEffect,
  useRef,
} from "react";
import { createPortal } from "react-dom";

const FOCUSABLE_SELECTOR = [
  "a[href]",
  "area[href]",
  "button:not([disabled])",
  "input:not([disabled]):not([type='hidden'])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  "iframe",
  "object",
  "embed",
  "[contenteditable='true']",
  "[tabindex]:not([tabindex='-1'])",
].join(",");

const dialogStack: symbol[] = [];

export interface DialogPrimitiveProps {
  children: ReactNode;
  labelledBy: string;
  describedBy?: string;
  onDismiss: () => void;
  initialFocusRef?: RefObject<HTMLElement | null>;
  returnFocusRef?: RefObject<HTMLElement | null>;
  dismissOnScrimClick?: boolean;
  scrimClassName?: string;
  className?: string;
}

function isVisible(element: HTMLElement): boolean {
  const style = window.getComputedStyle(element);
  return (
    element.getClientRects().length > 0 &&
    style.visibility !== "hidden" &&
    style.display !== "none" &&
    !element.closest("[inert]")
  );
}

function focusElement(element: HTMLElement | null): boolean {
  if (!element?.isConnected || !isVisible(element)) return false;
  element.focus({ preventScroll: true });
  return document.activeElement === element;
}

function focusableElements(container: HTMLElement): HTMLElement[] {
  return [...container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)].filter(
    isVisible,
  );
}

export function DialogPrimitive({
  children,
  labelledBy,
  describedBy,
  onDismiss,
  initialFocusRef,
  returnFocusRef,
  dismissOnScrimClick = true,
  scrimClassName = "dialog-scrim",
  className = "dialog-surface",
}: DialogPrimitiveProps) {
  const dialogRef = useRef<HTMLElement | null>(null);
  const scrimRef = useRef<HTMLDivElement | null>(null);
  const dialogId = useRef(Symbol("dialog")).current;
  const dismissRef = useRef(onDismiss);
  const initialRef = useRef(initialFocusRef);
  const returnRef = useRef(returnFocusRef);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  dismissRef.current = onDismiss;
  initialRef.current = initialFocusRef;
  returnRef.current = returnFocusRef;

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;

    if (!previouslyFocusedRef.current && document.activeElement instanceof HTMLElement) {
      previouslyFocusedRef.current = document.activeElement;
    }

    const scrim = scrimRef.current;
    const inertSiblings = scrim?.parentElement
      ? [...scrim.parentElement.children]
          .filter((element): element is HTMLElement => element instanceof HTMLElement && element !== scrim)
          .map((element) => ({ element, wasInert: element.inert }))
      : [];
    inertSiblings.forEach(({ element }) => { element.inert = true; });

    dialogStack.push(dialogId);

    const moveFocusInside = (preferLast = false) => {
      const candidates = focusableElements(dialog);
      const requestedInitialTarget = initialRef.current?.current ?? null;
      const candidate = preferLast
        ? candidates.at(-1)
        : requestedInitialTarget && dialog.contains(requestedInitialTarget)
          ? requestedInitialTarget
          : candidates[0];

      if (!focusElement(candidate ?? null)) {
        focusElement(preferLast ? candidates.at(-1) ?? null : candidates[0] ?? null) ||
          focusElement(dialog);
      }
    };

    moveFocusInside();

    const isTopDialog = () => dialogStack.at(-1) === dialogId;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (!isTopDialog()) return;

      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        dismissRef.current();
        return;
      }

      if (event.key !== "Tab") return;

      const candidates = focusableElements(dialog);
      if (candidates.length === 0) {
        event.preventDefault();
        focusElement(dialog);
        return;
      }

      const first = candidates[0];
      const last = candidates.at(-1) ?? first;
      const active = document.activeElement;

      if (!dialog.contains(active)) {
        event.preventDefault();
        focusElement(event.shiftKey ? last : first);
      } else if (event.shiftKey && (active === first || active === dialog)) {
        event.preventDefault();
        focusElement(last);
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        focusElement(first);
      }
    };

    const handleFocusIn = (event: FocusEvent) => {
      if (!isTopDialog() || dialog.contains(event.target as Node)) return;
      moveFocusInside();
    };

    document.addEventListener("keydown", handleKeyDown, true);
    document.addEventListener("focusin", handleFocusIn, true);

    return () => {
      document.removeEventListener("keydown", handleKeyDown, true);
      document.removeEventListener("focusin", handleFocusIn, true);

      const stackIndex = dialogStack.lastIndexOf(dialogId);
      if (stackIndex >= 0) dialogStack.splice(stackIndex, 1);

      const explicitReturnTarget = returnRef.current?.current ?? null;

      inertSiblings.forEach(({ element, wasInert }) => { element.inert = wasInert; });

      queueMicrotask(() => {
        if (dialogStack.includes(dialogId)) return;
        if (!focusElement(explicitReturnTarget)) {
          focusElement(previouslyFocusedRef.current);
        }
      });
    };
  }, [dialogId]);

  const handleScrimMouseDown = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) return;
    event.preventDefault();
    if (dismissOnScrimClick) dismissRef.current();
  };

  return createPortal(
    <div
      className={scrimClassName}
      ref={scrimRef}
      role="presentation"
      onMouseDown={handleScrimMouseDown}
    >
      <section
        className={className}
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={labelledBy}
        aria-describedby={describedBy}
        tabIndex={-1}
      >
        {children}
      </section>
    </div>,
    document.body,
  );
}
