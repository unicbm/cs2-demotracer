export type Language = "zh" | "en";
export type Theme = "system" | "light" | "dark";
export type SideChoice = "both" | "t" | "ct";

export type Phase =
  | "idle"
  | "analyzing"
  | "analysisFailed"
  | "openingArchive"
  | "archive"
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
  demoSha256: string;
  map: string;
  tickRate: number;
  rowCount: number;
  sourceModifiedAtMs?: number | null;
  sourceSizeBytes?: number | string | null;
  durationSeconds: number;
  demoPatchVersion?: number | string | null;
  demoVersionName?: string | null;
  serverName?: string | null;
  demoSource?: DemoSource | null;
  converterVersion: string;
  players: AnalysisPlayerSummary[];
  score?: MatchScoreSummary | null;
  rounds: RoundInfo[];
}

export interface MatchTeamScore {
  score: number;
  name?: string | null;
}

export interface MatchScoreSummary {
  teamA: MatchTeamScore;
  teamB: MatchTeamScore;
  status?: "final" | "completed" | "snapshot" | string;
}

export interface DemoSource {
  name: string;
  evidence: "serverName" | "fileName" | string;
}

export interface AnalysisPlayerSummary {
  name: string;
  steamId: string;
  side: string;
  team: "a" | "b" | "unknown" | string;
  teamName?: string | null;
  rounds: number;
  rows: number;
  score?: number | null;
  kills?: number | null;
  deaths?: number | null;
  assists?: number | null;
  mvps?: number | null;
}

export interface LibraryPlayerSummary {
  name: string;
  steamId: string;
  side: string;
  team: "a" | "b" | "unknown" | string;
  rounds: number;
  files: number;
  teamName?: string | null;
  score?: number | null;
  kills?: number | null;
  deaths?: number | null;
  assists?: number | null;
  mvps?: number | null;
}

export interface DemoLibraryEntry {
  root: string;
  manifestPath: string;
  demoPath: string;
  demoId: string;
  demoSha256: string;
  displayName?: string | null;
  map: string;
  tickRate: number;
  abi: number;
  formatVersion: number;
  compatibility: "current" | "supported" | "legacy" | "unsupported" | string;
  modifiedAtMs: number;
  rounds: number;
  files: number;
  players: LibraryPlayerSummary[];
  score?: MatchScoreSummary | null;
  scoreIsSnapshot?: boolean;
  metadataStatus?: "current" | "missing" | "stale" | "invalid" | string;
  sourcePath?: string | null;
  sourceAvailable?: boolean;
  sourceModifiedAtMs?: number | null;
  sourceSizeBytes?: number | string | null;
  durationSeconds?: number | null;
  demoPatchVersion?: number | string | null;
  demoVersionName?: string | null;
  serverName?: string | null;
  demoSource?: DemoSource | null;
  converterVersion?: string | null;
}

export interface RefreshArchiveMetadataResult {
  manifestPath: string;
  infoPath: string;
  displayName: string;
  sourcePath: string;
}

export interface ResolveArchiveSourceResult {
  sourcePath: string;
}

export interface RefreshLibraryMetadataResult {
  demosScanned: number;
  demosMatched: number;
  archivesUpdated: number;
  archivesCurrent: number;
  archivesUnmatched: number;
  sourceUnmatched: number;
  sourcePaths: Record<string, string>;
  failures: string[];
}

export interface ImportArchivesResult {
  archivesFound: number;
  archivesImported: number;
  duplicatesSkipped: number;
  archivesRejected: number;
  failures: string[];
}

export interface DemoLibrarySkipped {
  path: string;
  message: string;
}

export interface DemoLibraryScan {
  root: string;
  entries: DemoLibraryEntry[];
  skipped: DemoLibrarySkipped[];
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
  side?: string | null;
  matchTeam?: string | null;
  teamName?: string | null;
  score?: number | null;
  kills?: number | null;
  deaths?: number | null;
  assists?: number | null;
  mvps?: number | null;
}

export interface RoundOutputSummary {
  round: number;
  files: number;
}

export interface CosmeticSummary {
  requested: boolean | null;
  stickerRequested: boolean | null;
  charmRequested: boolean | null;
  files: number;
  stickerFiles: number;
  charmFiles: number;
  preset?: "basic" | "full" | null;
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
    requested: boolean | null;
    sidecars: number;
  };
  cosmetics: CosmeticSummary;
  commands: {
    goRound: string;
    goSequence: string;
    round: string;
    sequence: string;
    cosmeticRound?: string | null;
    cosmeticSequence?: string | null;
  };
}

export interface ManifestArchiveIssue {
  code: string;
  severity: "warning" | "error";
  message: string;
  round?: number | null;
  path?: string | null;
}

export interface ManifestTeamEconomy {
  side: string;
  players: number;
  roundStartEquipmentValue: number;
  equipmentValueTotal: number;
  moneySavedTotal: number;
  cashSpentThisRound: number;
  class: string;
}

export interface ManifestArchiveRound {
  round: number;
  files: number;
  tFiles: number;
  ctFiles: number;
  cosmeticFiles: number;
  stickerFiles: number;
  charmFiles: number;
  durationSeconds?: number | null;
  pistolRound?: boolean | null;
  cutReason?: string | null;
  tEconomy?: ManifestTeamEconomy | null;
  ctEconomy?: ManifestTeamEconomy | null;
  scoreboard?: {
    tScore: number;
    ctScore: number;
    tTeamName?: string | null;
    ctTeamName?: string | null;
  } | null;
  bombPlantedSeconds?: number | null;
  ticks: number;
  subticks: number;
  hifiEvents: number;
  inventorySnapshots: number;
  sequenceLength: number;
  available: boolean;
  commands: ConversionSummary["commands"];
}

export interface ManifestArchive {
  root: string;
  manifestPath: string;
  demoPath: string;
  demoId: string;
  demoSha256: string;
  map: string;
  tickRate: number;
  abi: number;
  formatVersion: number;
  compatibility: string;
  totalFiles: number;
  playableFiles: number;
  outputBytes: number | string;
  playable: boolean;
  players: PlayerSummary[];
  voice: {
    requested: boolean | null;
    sidecars: number;
    rounds: number[];
  };
  cosmetics: CosmeticSummary;
  rounds: ManifestArchiveRound[];
  issues: ManifestArchiveIssue[];
  displayName: string;
  metadataStatus: string;
  sourcePath?: string | null;
  sourceModifiedAtMs?: number | null;
  durationSeconds?: number | null;
  demoPatchVersion?: number | null;
  demoVersionName?: string | null;
  demoSource?: DemoSource | null;
  score?: MatchScoreSummary | null;
  converterVersion?: string | null;
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
