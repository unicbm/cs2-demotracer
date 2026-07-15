export type Language = "zh" | "en";
export type Theme = "system" | "light" | "dark";
export type SideChoice = "both" | "t" | "ct";

export type Phase =
  | "idle"
  | "analyzing"
  | "analysisFailed"
  | "selecting"
  | "converting"
  | "validationFailed"
  | "complete";

export type ProgressPhase =
  | "preparing"
  | "parsing"
  | "analyzing"
  | "writing"
  | "artifacts"
  | "voice"
  | "validating"
  | "complete";

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

export interface OutputPreflight {
  root: string;
  exists: boolean;
}

export interface CommandErrorDto {
  code: string;
  message: string;
  path?: string;
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
  roundsExported: number;
  firstExportedRound?: number | null;
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
    goRound: string;
    goSequence: string;
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

export type LogLevel = "info" | "warning" | "error";

export interface ActivityLogEntry {
  level: LogLevel;
  message: string;
}

export interface ProgressState {
  phase: ProgressPhase;
  message: string;
  written: number;
  estimated: number;
  unit: "playerFiles" | "artifacts" | null;
  currentRound?: number;
  completedRounds: number;
  selectedRounds: number;
  currentItem?: string;
  log: ActivityLogEntry[];
  warnings: string[];
  announcement: string;
}

export type TaskPhase = "parsing" | "analyzing" | "exporting" | "voice" | "validating" | "complete";

export type ConversionProgressEvent =
  | { event: "analysisStarted" }
  | { event: "analysisFinished"; rounds: number; selectedRounds: number; estimatedFiles: number }
  | { event: "roundSkipped"; round: number; reason: string }
  | { event: "roundStarted"; round: number; estimatedPlayers: number }
  | { event: "playerSkipped"; round: number; steamId: string; reason: string }
  | {
      event: "playerWritten";
      round: number;
      steamId: string;
      playerName: string;
      side: string;
      path: string;
      ticks: number;
      subticks: number;
    }
  | { event: "roundFinished"; round: number; files: number }
  | { event: "artifactsWritingStarted"; root: string; artifacts: number }
  | { event: "artifactWritten"; path: string; artifactKind: string }
  | { event: "finished"; root: string; manifestPath: string; filesWritten: number };

export type TaskEvent =
  | { kind: "phase"; phase: TaskPhase }
  | { kind: "log"; level: LogLevel; message: string }
  | { kind: "progress"; progress: ConversionProgressEvent };
