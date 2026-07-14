import type { SVGProps } from "react";

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

const base = (size: number): SVGProps<SVGSVGElement> => ({
  width: size,
  height: size,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.7,
  strokeLinecap: "round",
  strokeLinejoin: "round",
  "aria-hidden": true,
});

export function TraceMark({ size = 34, ...props }: IconProps) {
  return (
    <svg {...base(size)} viewBox="0 0 36 36" {...props}>
      <path d="M4.5 24.7C8.9 24.7 8.6 11.2 13 11.2s4.1 13.5 8.5 13.5 4.1-13.5 8.5-13.5" />
      <circle cx="4.5" cy="24.7" r="2.1" fill="currentColor" stroke="none" />
      <circle cx="13" cy="11.2" r="2.1" fill="currentColor" stroke="none" />
      <circle cx="21.5" cy="24.7" r="2.1" fill="currentColor" stroke="none" />
      <circle cx="30" cy="11.2" r="2.1" fill="currentColor" stroke="none" />
    </svg>
  );
}

export function FolderIcon({ size = 20, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M3.5 6.8h6l1.8 2h9.2v9.4a1.8 1.8 0 0 1-1.8 1.8H5.3a1.8 1.8 0 0 1-1.8-1.8V6.8Z" />
      <path d="M3.5 9h17" />
    </svg>
  );
}

export function ArrowIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M5 12h13.5M14 7.5l4.5 4.5-4.5 4.5" />
    </svg>
  );
}

export function SlidersIcon({ size = 19, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4 7h5m4 0h7M9 4v6M4 17h9m4 0h3m-3-3v6" />
    </svg>
  );
}

export function CheckIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m5 12.3 4.2 4.2L19 6.8" />
    </svg>
  );
}

export function ChevronIcon({ size = 17, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m7.5 9.5 4.5 4.5 4.5-4.5" />
    </svg>
  );
}

export function CopyIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <rect x="8" y="8" width="11" height="11" rx="2" />
      <path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" />
    </svg>
  );
}

export function SunIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <circle cx="12" cy="12" r="3.5" />
      <path d="M12 2.8v2M12 19.2v2M2.8 12h2M19.2 12h2M5.5 5.5l1.4 1.4M17.1 17.1l1.4 1.4M18.5 5.5l-1.4 1.4M6.9 17.1l-1.4 1.4" />
    </svg>
  );
}

export function MoonIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M19.4 15.1A8 8 0 0 1 8.9 4.6 8 8 0 1 0 19.4 15Z" />
    </svg>
  );
}

export function CloseIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="m6 6 12 12M18 6 6 18" />
    </svg>
  );
}

export function AlertIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M12 3.2 21 19H3L12 3.2Z" />
      <path d="M12 9v4.5M12 16.8v.1" />
    </svg>
  );
}

export function ReplayIcon({ size = 18, ...props }: IconProps) {
  return (
    <svg {...base(size)} {...props}>
      <path d="M4.5 9A8 8 0 1 1 4 14" />
      <path d="M4.5 4.8V9h4.2" />
    </svg>
  );
}
