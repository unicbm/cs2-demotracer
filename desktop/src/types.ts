export type Language = "zh" | "en";
export type Theme = "system" | "light" | "dark";
export type SideChoice = "both" | "t" | "ct";
export type Phase = "idle" | "analyzing" | "ready" | "converting" | "complete";

export interface RoundInfo {
  round: number;
  startTick: number;
  endTick: number;
  durationSeconds: number;
  tPlayers: number;
  ctPlayers: number;
  totalPlayers: number;
  validRows: number;
  status: "recommended" | "suspicious";
  problems: string[];
  selectedByDefault: boolean;
}

export interface AnalysisResult {
  analysisId: string;
  sourcePath: string;
  fileName: string;
  outputDemoId: string;
  map: string;
  tickRate: number;
  rowCount: number;
  rounds: RoundInfo[];
}

export interface PlayerSummary {
  team: string | number;
  name: string;
  steamId: string;
  rounds: number;
  files: number;
}

export interface RoundOutputSummary {
  round: number;
  files: number;
}

export interface ConversionSummary {
  root: string;
  manifestPath: string;
  filesWritten: number;
  validatedFiles: number;
  outputBytes: number | string;
  rounds: RoundOutputSummary[];
  players: PlayerSummary[];
  voice: {
    requested: boolean;
    sidecars: number;
  };
  cosmetics: {
    files: number;
    stickerFiles: number;
    charmFiles: number;
    preset?: "basic" | "full" | null;
  };
  commands: {
    round: string;
    sequence: string;
    cosmeticRound?: string | null;
    cosmeticSequence?: string | null;
  };
}

export interface ConverterSettings {
  side: SideChoice;
  fullRound: boolean;
  freezePrerollSeconds: number;
  exportVoice: boolean;
  exportCosmetics: boolean;
  exportStickers: boolean;
  exportCharms: boolean;
  includeSuspicious: boolean;
}

export interface CosmeticConsent {
  acknowledgeGsltRisk: boolean;
  acceptExportDisclaimer: boolean;
  phrase: string;
}

export interface ProgressState {
  phase: string;
  message: string;
  written: number;
  estimated: number;
  currentRound?: number;
  log: string[];
}

export type TaskEvent = Record<string, unknown>;
