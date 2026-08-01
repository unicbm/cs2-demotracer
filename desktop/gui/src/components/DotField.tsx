/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import {
  memo,
  useEffect,
  useRef,
  type HTMLAttributes,
} from "react";

import "./DotField.css";

const TWO_PI = Math.PI * 2;

interface Dot {
  anchorX: number;
  anchorY: number;
  screenX: number;
  screenY: number;
}

interface DotFieldProps extends Omit<HTMLAttributes<HTMLDivElement>, "children"> {
  dotRadius?: number;
  dotSpacing?: number;
  cursorRadius?: number;
  bulgeStrength?: number;
  glowRadius?: number;
  sparkle?: boolean;
  waveAmplitude?: number;
  gradientFrom?: string;
  gradientTo?: string;
  glowColor?: string;
}

const DotField = memo(function DotField({
  dotRadius = 1.5,
  dotSpacing = 14,
  cursorRadius = 500,
  bulgeStrength = 67,
  glowRadius = 160,
  sparkle = false,
  waveAmplitude = 0,
  gradientFrom = "rgba(20, 184, 166, 0.24)",
  gradientTo = "rgba(34, 211, 238, 0.12)",
  glowColor = "rgba(20, 184, 166, 0.16)",
  className,
  ...rest
}: DotFieldProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const glowRef = useRef<SVGCircleElement>(null);
  const dotsRef = useRef<Dot[]>([]);
  const pointerRef = useRef({
    x: -9999,
    y: -9999,
    previousX: -9999,
    previousY: -9999,
    speed: 0,
  });
  const animationRef = useRef<number | null>(null);
  const sizeRef = useRef({ width: 0, height: 0 });
  const engagementRef = useRef(0);
  const glowOpacityRef = useRef(0);
  const propsRef = useRef({
    dotRadius,
    dotSpacing,
    cursorRadius,
    bulgeStrength,
    sparkle,
    waveAmplitude,
    gradientFrom,
    gradientTo,
  });
  const rebuildRef = useRef<(() => void) | null>(null);
  const glowIdRef = useRef(`dot-field-glow-${Math.random().toString(36).slice(2, 9)}`);

  propsRef.current = {
    dotRadius,
    dotSpacing,
    cursorRadius,
    bulgeStrength,
    sparkle,
    waveAmplitude,
    gradientFrom,
    gradientTo,
  };

  useEffect(() => {
    const canvas = canvasRef.current;
    const glow = glowRef.current;
    const container = canvas?.parentElement;
    if (!canvas || !container) return;

    const context = canvas.getContext("2d", { alpha: true });
    if (!context) return;
    const activeCanvas: HTMLCanvasElement = canvas;
    const activeContainer: HTMLElement = container;
    const activeContext: CanvasRenderingContext2D = context;

    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    let frame = 0;
    let resizeTimer: ReturnType<typeof setTimeout> | undefined;

    function buildDots(width: number, height: number) {
      const props = propsRef.current;
      const step = props.dotRadius + props.dotSpacing;
      const columns = Math.floor(width / step);
      const rows = Math.floor(height / step);
      const padX = (width % step) / 2;
      const padY = (height % step) / 2;
      const dots = new Array<Dot>(rows * columns);
      let index = 0;

      for (let row = 0; row < rows; row += 1) {
        for (let column = 0; column < columns; column += 1) {
          const anchorX = padX + column * step + step / 2;
          const anchorY = padY + row * step + step / 2;
          dots[index] = {
            anchorX,
            anchorY,
            screenX: anchorX,
            screenY: anchorY,
          };
          index += 1;
        }
      }
      dotsRef.current = dots;
    }

    function draw(animate: boolean) {
      const { width, height } = sizeRef.current;
      const props = propsRef.current;
      const pointer = pointerRef.current;
      const dots = dotsRef.current;
      const time = frame * 0.02;

      if (animate) {
        const targetEngagement = Math.min(pointer.speed / 5, 1);
        engagementRef.current += (targetEngagement - engagementRef.current) * 0.06;
        if (engagementRef.current < 0.001) engagementRef.current = 0;
        glowOpacityRef.current += (engagementRef.current - glowOpacityRef.current) * 0.08;
      } else {
        engagementRef.current = 0;
        glowOpacityRef.current = 0;
      }

      if (glow) {
        glow.setAttribute("cx", String(pointer.x));
        glow.setAttribute("cy", String(pointer.y));
        glow.style.opacity = String(glowOpacityRef.current);
      }

      activeContext.clearRect(0, 0, width, height);
      const gradient = activeContext.createLinearGradient(0, 0, width, height);
      gradient.addColorStop(0, props.gradientFrom);
      gradient.addColorStop(1, props.gradientTo);
      activeContext.fillStyle = gradient;
      activeContext.beginPath();

      const cursorRadiusSquared = props.cursorRadius * props.cursorRadius;
      const radius = props.dotRadius / 2;
      const engagement = engagementRef.current;

      dots.forEach((dot, index) => {
        const deltaX = pointer.x - dot.anchorX;
        const deltaY = pointer.y - dot.anchorY;
        const distanceSquared = deltaX * deltaX + deltaY * deltaY;

        if (distanceSquared < cursorRadiusSquared && engagement > 0.01) {
          const distance = Math.sqrt(distanceSquared);
          const pressure = 1 - distance / props.cursorRadius;
          const push = pressure * pressure * props.bulgeStrength * engagement;
          const angle = Math.atan2(deltaY, deltaX);
          dot.screenX += (dot.anchorX - Math.cos(angle) * push - dot.screenX) * 0.15;
          dot.screenY += (dot.anchorY - Math.sin(angle) * push - dot.screenY) * 0.15;
        } else {
          dot.screenX += (dot.anchorX - dot.screenX) * 0.1;
          dot.screenY += (dot.anchorY - dot.screenY) * 0.1;
        }

        let drawX = dot.screenX;
        let drawY = dot.screenY;
        if (props.waveAmplitude > 0) {
          drawY += Math.sin(dot.anchorX * 0.03 + time) * props.waveAmplitude;
          drawX += Math.cos(dot.anchorY * 0.03 + time * 0.7) * props.waveAmplitude * 0.5;
        }

        const sparkles = props.sparkle && (((index * 2654435761) ^ (frame >> 3)) >>> 0) % 100 < 3;
        const drawRadius = sparkles ? radius * 1.8 : radius;
        activeContext.moveTo(drawX + drawRadius, drawY);
        activeContext.arc(drawX, drawY, drawRadius, 0, TWO_PI);
      });

      activeContext.fill();
    }

    function resize() {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        const rect = activeContainer.getBoundingClientRect();
        const width = Math.max(1, rect.width);
        const height = Math.max(1, rect.height);
        activeCanvas.width = Math.round(width * dpr);
        activeCanvas.height = Math.round(height * dpr);
        activeCanvas.style.width = `${width}px`;
        activeCanvas.style.height = `${height}px`;
        activeContext.setTransform(dpr, 0, 0, dpr, 0, 0);
        sizeRef.current = {
          width,
          height,
        };
        buildDots(width, height);
        draw(false);
      }, 80);
    }

    function updatePointer(event: PointerEvent) {
      const rect = activeContainer.getBoundingClientRect();
      pointerRef.current.x = event.clientX - rect.left;
      pointerRef.current.y = event.clientY - rect.top;
    }

    function updatePointerSpeed() {
      const pointer = pointerRef.current;
      const deltaX = pointer.previousX - pointer.x;
      const deltaY = pointer.previousY - pointer.y;
      const distance = Math.sqrt(deltaX * deltaX + deltaY * deltaY);
      pointer.speed += (distance - pointer.speed) * 0.5;
      if (pointer.speed < 0.001) pointer.speed = 0;
      pointer.previousX = pointer.x;
      pointer.previousY = pointer.y;
    }

    function tick() {
      frame += 1;
      draw(true);
      animationRef.current = requestAnimationFrame(tick);
    }

    resize();
    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(activeContainer);
    rebuildRef.current = () => {
      const { width, height } = sizeRef.current;
      if (width > 0 && height > 0) {
        buildDots(width, height);
        draw(false);
      }
    };

    if (reducedMotion) {
      return () => {
        clearTimeout(resizeTimer);
        resizeObserver.disconnect();
      };
    }

    window.addEventListener("pointermove", updatePointer, { passive: true });
    const speedInterval = window.setInterval(updatePointerSpeed, 20);
    animationRef.current = requestAnimationFrame(tick);

    return () => {
      if (animationRef.current != null) cancelAnimationFrame(animationRef.current);
      window.clearInterval(speedInterval);
      clearTimeout(resizeTimer);
      resizeObserver.disconnect();
      window.removeEventListener("pointermove", updatePointer);
    };
  }, []);

  useEffect(() => {
    rebuildRef.current?.();
  }, [dotRadius, dotSpacing]);

  return (
    <div
      className={["dot-field-container", className].filter(Boolean).join(" ")}
      {...rest}
    >
      <canvas
        ref={canvasRef}
        style={{ position: "absolute", inset: 0, width: "100%", height: "100%" }}
      />
      <svg
        style={{
          position: "absolute",
          inset: 0,
          width: "100%",
          height: "100%",
          pointerEvents: "none",
        }}
      >
        <defs>
          <radialGradient id={glowIdRef.current}>
            <stop offset="0%" stopColor={glowColor} />
            <stop offset="100%" stopColor="transparent" />
          </radialGradient>
        </defs>
        <circle
          ref={glowRef}
          cx="-9999"
          cy="-9999"
          r={glowRadius}
          fill={`url(#${glowIdRef.current})`}
          style={{ opacity: 0, willChange: "opacity" }}
        />
      </svg>
    </div>
  );
});

export default DotField;
