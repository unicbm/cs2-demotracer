/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

(() => {
  if (window.top !== window || window.location.origin !== "https://inventory.cstrike.app") return;

  const installScrollbarStyle = () => {
    if (document.getElementById("demotracer-hidden-scrollbars")) return;
    const style = document.createElement("style");
    style.id = "demotracer-hidden-scrollbars";
    style.textContent = `
      html, body, * { scrollbar-width: none !important; }
      *::-webkit-scrollbar { width: 0 !important; height: 0 !important; display: none !important; }
    `;
    document.head.append(style);
  };
  if (document.head) installScrollbarStyle();
  else document.addEventListener("DOMContentLoaded", installScrollbarStyle, { once: true });

  const stateBridgeKey = "__demotracerInventoryStateBridgeV1";
  const inventoryStateBridge = window[stateBridgeKey] || (() => {
    const bridge = { sync: null };
    Object.defineProperty(window, stateBridgeKey, { value: bridge });
    const originalAddEventListener = EventTarget.prototype.addEventListener;
    let restored = false;
    let patchedAddEventListener;
    const restore = () => {
      if (restored) return;
      restored = true;
      if (EventTarget.prototype.addEventListener === patchedAddEventListener) {
        EventTarget.prototype.addEventListener = originalAddEventListener;
      }
    };
    patchedAddEventListener = function demotracerCaptureSyncTarget(type, listener, options) {
      const result = originalAddEventListener.call(this, type, listener, options);
      if (type === "syncerror"
        && Array.isArray(this?.queue)
        && typeof this.syncedAt === "number"
        && typeof this.isSyncing === "boolean"
        && typeof this.dispatchEvent === "function") {
        bridge.sync = this;
        restore();
      }
      return result;
    };
    EventTarget.prototype.addEventListener = patchedAddEventListener;
    window.setTimeout(restore, 15_000);
    return bridge;
  })();

  const dedupe = __DTR_DEDUPE_SOURCE__;
  const configBytes = Uint8Array.from(
    atob("__DTR_BATCH_CONFIG_BASE64__"),
    (character) => character.charCodeAt(0),
  );
  const embeddedConfig = JSON.parse(new TextDecoder().decode(configBytes));
  const rootId = "demotracer-inventory-batch";
  const pendingConfigKey = "demotracer:inventory-batch:pending-config";
  const runStateKeyFor = (run) => `demotracer:inventory-batch:${run}`;
  let config = embeddedConfig;
  if (window.sessionStorage.getItem(runStateKeyFor(embeddedConfig.run)) !== null) {
    try {
      const pendingConfig = JSON.parse(window.sessionStorage.getItem(pendingConfigKey) || "null");
      if (pendingConfig
        && typeof pendingConfig.run === "string"
        && Array.isArray(pendingConfig.items)
        && pendingConfig.copy) config = pendingConfig;
    } catch {
      window.sessionStorage.removeItem(pendingConfigKey);
    }
  } else {
    window.sessionStorage.setItem(pendingConfigKey, JSON.stringify(embeddedConfig));
  }
  const runStateKey = `demotracer:inventory-batch:${config.run}`;
  const previousRunState = window.sessionStorage.getItem(runStateKey);
  if (previousRunState === "running" || previousRunState === "complete" || previousRunState === "failed") return;
  const interpolate = (text, values) => Object.entries(values).reduce(
    (result, [key, value]) => result.replace(`{${key}}`, String(value)),
    text,
  );

  function createElement(tag, text) {
    const element = document.createElement(tag);
    if (text !== undefined) element.textContent = text;
    return element;
  }

  function applyStyles(element, styles) {
    Object.assign(element.style, styles);
    return element;
  }

  function render() {
    window.sessionStorage.setItem(runStateKey, "running");
    document.getElementById(rootId)?.remove();

    const root = applyStyles(createElement("section"), {
      position: "fixed",
      zIndex: "2147483647",
      top: "12px",
      right: "12px",
      display: "grid",
      width: "min(380px, calc(100vw - 24px))",
      padding: "11px 12px 10px",
      gap: "7px",
      color: "#f5f7fa",
      background: "rgba(17, 21, 27, 0.96)",
      border: "1px solid rgba(255, 255, 255, 0.16)",
      borderRadius: "6px",
      boxShadow: "0 9px 26px rgba(0, 0, 0, 0.3)",
      fontFamily: "Inter, system-ui, sans-serif",
    });
    root.id = rootId;
    root.setAttribute("role", "status");
    root.setAttribute("aria-live", "polite");

    const heading = applyStyles(createElement("div"), {
      display: "flex",
      alignItems: "center",
      justifyContent: "space-between",
      gap: "8px",
    });
    const title = applyStyles(createElement("strong", config.copy.title), {
      overflow: "hidden",
      fontSize: "12px",
      lineHeight: "1.2",
      letterSpacing: "0.02em",
      textOverflow: "ellipsis",
      whiteSpace: "nowrap",
    });
    const count = applyStyles(createElement("span", String(config.items.length)), {
      flex: "none",
      minWidth: "22px",
      padding: "2px 6px",
      color: "#c9d3df",
      background: "rgba(255, 255, 255, 0.08)",
      borderRadius: "999px",
      fontSize: "11px",
      fontWeight: "700",
      textAlign: "center",
    });
    const message = applyStyles(createElement("div", config.copy.checking), {
      color: "#d9e0e9",
      fontSize: "12.5px",
      lineHeight: "1.4",
    });
    const track = applyStyles(createElement("div"), {
      position: "relative",
      height: "3px",
      overflow: "hidden",
      background: "rgba(255, 255, 255, 0.1)",
      borderRadius: "999px",
    });
    const bar = applyStyles(createElement("i"), {
      position: "absolute",
      inset: "0 auto 0 0",
      width: "38%",
      background: "#e07a32",
      borderRadius: "999px",
      transform: "translateX(-110%)",
    });
    const actions = applyStyles(createElement("div"), {
      display: "none",
      justifyContent: "flex-end",
      gap: "6px",
    });
    const buttonStyle = {
      minHeight: "29px",
      padding: "4px 9px",
      color: "#f5f7fa",
      background: "rgba(255, 255, 255, 0.09)",
      border: "1px solid rgba(255, 255, 255, 0.2)",
      borderRadius: "4px",
      cursor: "pointer",
      font: "600 12px/1.2 Inter, system-ui, sans-serif",
    };
    const signIn = applyStyles(createElement("button", config.copy.signIn), {
      ...buttonStyle,
      background: "#245f9e",
      borderColor: "#3982c9",
    });
    const retry = applyStyles(createElement("button", config.copy.retry), buttonStyle);
    signIn.type = "button";
    retry.type = "button";
    heading.append(title, count);
    track.append(bar);
    actions.append(signIn, retry);
    root.append(heading, message, track, actions);
    document.body.append(root);

    let animation = bar.animate?.(
      [
        { transform: "translateX(-110%)" },
        { transform: "translateX(290%)" },
      ],
      { duration: 950, iterations: Infinity, easing: "ease-in-out" },
    );

    const setMessage = (text, kind = "working") => {
      message.textContent = text;
      message.style.color = kind === "error" ? "#ffd2cc" : kind === "success" ? "#c8f5d4" : "#d9e0e9";
      root.style.borderColor = kind === "error"
        ? "rgba(232, 85, 70, 0.5)"
        : kind === "success"
          ? "rgba(71, 177, 99, 0.5)"
          : "rgba(255, 255, 255, 0.16)";
      bar.style.background = kind === "error" ? "#e95f50" : kind === "success" ? "#45b765" : "#e07a32";
    };
    const stopProgress = (complete = false) => {
      animation?.cancel();
      animation = null;
      bar.style.width = complete ? "100%" : "0";
      bar.style.transform = "none";
    };
    const removeSoon = (delay) => {
      window.setTimeout(() => root.remove(), delay);
    };
    const clearPendingConfig = () => {
      try {
        const pendingConfig = JSON.parse(window.sessionStorage.getItem(pendingConfigKey) || "null");
        if (pendingConfig?.run === config.run) window.sessionStorage.removeItem(pendingConfigKey);
      } catch {
        window.sessionStorage.removeItem(pendingConfigKey);
      }
    };
    const wait = (milliseconds) => new Promise((resolve) => window.setTimeout(resolve, milliseconds));

    async function refreshVisibleInventory(syncedAt) {
      const sync = inventoryStateBridge.sync;
      if (!sync || !Number.isFinite(syncedAt)) return false;

      const idleDeadline = Date.now() + 2_000;
      while ((sync.isSyncing || sync.queue.length > 0) && Date.now() < idleDeadline) {
        await wait(50);
      }
      if (sync.isSyncing || sync.queue.length > 0) return false;

      let resyncModal = null;
      const observer = new MutationObserver((records) => {
        for (const record of records) {
          for (const node of record.addedNodes) {
            const buttons = node instanceof HTMLElement
              ? node.querySelectorAll("button")
              : [];
            if (!(node instanceof HTMLElement)
              || node.parentElement !== document.body
              || !node.classList.contains("fixed")
              || !node.classList.contains("z-50")
              || !node.classList.contains("min-h-full")
              || !node.classList.contains("w-full")
              || buttons.length !== 1
              || !buttons[0].disabled) continue;
            resyncModal = node;
            resyncModal.style.visibility = "hidden";
          }
        }
      });
      observer.observe(document.body, { childList: true });
      sync.dispatchEvent(new Event("syncerror"));

      const refreshDeadline = Date.now() + 8_000;
      let closeButton = null;
      while (Date.now() < refreshDeadline) {
        closeButton = resyncModal?.querySelector("button:not([disabled])") || null;
        if (sync.syncedAt === syncedAt && closeButton) break;
        await wait(50);
      }
      const refreshed = sync.syncedAt === syncedAt && closeButton !== null;
      if (refreshed) {
        closeButton.click();
        const closeDeadline = Date.now() + 500;
        while (resyncModal?.isConnected && Date.now() < closeDeadline) await wait(25);
      }
      observer.disconnect();

      const modalDismissed = !resyncModal?.isConnected;
      if (!modalDismissed && resyncModal) resyncModal.style.visibility = "";
      return refreshed && modalDismissed;
    }
    signIn.addEventListener("click", () => window.location.assign("/sign-in"));

    async function responseErrorDetail(response) {
      try {
        const body = (await response.text()).trim();
        if (!body || body.includes("<")) return "";
        let detail = body;
        if ((response.headers.get("content-type") || "").includes("application/json")) {
          const parsed = JSON.parse(body);
          detail = typeof parsed?.message === "string"
            ? parsed.message
            : typeof parsed?.error === "string"
              ? parsed.error
              : "";
        }
        return detail.replace(/\s+/g, " ").slice(0, 180);
      } catch {
        return "";
      }
    }

    async function latestSyncState() {
      const response = await fetch("/api/action/resync", {
        cache: "no-store",
        credentials: "same-origin",
        headers: { Accept: "application/json" },
        redirect: "manual",
      });
      const contentType = response.headers.get("content-type") || "";
      if (response.type === "opaqueredirect" || response.status === 0 || response.status === 401 || response.status === 403 || !contentType.includes("application/json")) {
        const error = new Error("auth-required");
        error.authRequired = true;
        throw error;
      }
      if (!response.ok) {
        const error = new Error(`resync-${response.status}`);
        error.status = response.status;
        throw error;
      }
      return response.json();
    }

    async function submitBatch() {
      for (let attempt = 0; attempt < 2; attempt += 1) {
        setMessage(config.copy.checking);
        const latest = await latestSyncState();
        const selected = dedupe.selectNewItems(config.items, latest.inventory);
        if (selected.items.length === 0) {
          return { added: 0, skipped: selected.skipped, syncedAt: latest.syncedAt };
        }
        setMessage(interpolate(config.copy.adding, {
          count: selected.items.length,
          skipped: selected.skipped,
        }));
        const response = await fetch("/api/action/sync", {
          method: "POST",
          credentials: "same-origin",
          headers: {
            Accept: "application/json",
            "Content-Type": "application/json",
          },
          redirect: "manual",
          body: JSON.stringify({
            syncedAt: latest.syncedAt,
            actions: selected.items.map((item) => ({ type: "add", item })),
          }),
        });
        if (response.status === 409 && attempt === 0) continue;
        if (response.type === "opaqueredirect" || response.status === 0 || response.status === 401 || response.status === 403) {
          const error = new Error("auth-required");
          error.authRequired = true;
          throw error;
        }
        if (!response.ok) {
          const error = new Error(`sync-${response.status}`);
          error.status = response.status;
          error.detail = await responseErrorDetail(response);
          throw error;
        }
        const responseData = await response.json().catch(() => null);
        return {
          added: selected.items.length,
          skipped: selected.skipped,
          syncedAt: responseData?.syncedAt,
        };
      }
      throw new Error("sync-conflict");
    }

    async function run() {
      actions.style.display = "none";
      signIn.style.display = "none";
      retry.style.display = "none";
      if (animation === null) {
        bar.style.width = "38%";
        animation = bar.animate?.(
          [{ transform: "translateX(-110%)" }, { transform: "translateX(290%)" }],
          { duration: 950, iterations: Infinity, easing: "ease-in-out" },
        );
      }
      try {
        const result = await submitBatch();
        setMessage(config.copy.refreshing);
        const refreshed = await refreshVisibleInventory(result.syncedAt);
        if (result.added === 0) {
          window.sessionStorage.setItem(runStateKey, "complete");
          clearPendingConfig();
          setMessage(
            `${interpolate(config.copy.duplicates, { skipped: result.skipped })}${refreshed ? "" : ` ${config.copy.refreshRequired}`}`,
            "success",
          );
          stopProgress(true);
          removeSoon(1600);
          return;
        }
        window.sessionStorage.setItem(runStateKey, "complete");
        clearPendingConfig();
        setMessage(
          `${interpolate(config.copy.success, {
            count: result.added,
            skipped: result.skipped,
          })}${refreshed ? "" : ` ${config.copy.refreshRequired}`}`,
          "success",
        );
        stopProgress(true);
        removeSoon(1600);
      } catch (error) {
        const authRequired = Boolean(error && error.authRequired);
        window.sessionStorage.setItem(runStateKey, authRequired ? "auth" : "failed");
        if (!authRequired) clearPendingConfig();
        const failure = interpolate(config.copy.failed, {
          status: error?.status || error?.message || "network",
        });
        setMessage(
          authRequired
            ? config.copy.authRequired
            : `${failure}${error?.detail ? ` ${error.detail}` : ""}`,
          "error",
        );
        stopProgress(false);
        actions.style.display = "flex";
        signIn.style.display = authRequired ? "inline-block" : "none";
        retry.style.display = "inline-block";
      }
    }

    retry.addEventListener("click", () => {
      window.sessionStorage.setItem(runStateKey, "running");
      void run();
    });
    void run();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", render, { once: true });
  } else {
    render();
  }
})();
