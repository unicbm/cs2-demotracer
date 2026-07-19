export type Language = "zh" | "en";
export type Theme = "system" | "light" | "dark";
export type SideChoice = "both" | "t" | "ct";
export type WorkspaceSection = "library" | "batch" | "settings" | "faq";
export type SubtickMode = "auto" | "off";

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
  | "decompressing"
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
  details?: PlayerDetails | null;
}

export interface PlayerDetails {
  headshotKills?: number | null;
  totalDamage?: number | null;
  statsRounds?: number | null;
  crosshairCodes?: string[];
  viewmodels?: ViewmodelEvidence[];
  cosmetics?: CosmeticEvidence[];
}

export interface ViewmodelEvidence {
  fov?: number | null;
  offsetX?: number | null;
  offsetY?: number | null;
  offsetZ?: number | null;
}

export interface CosmeticEvidence {
  kind: "weapon" | "knife" | "glove" | "agent" | string;
  side?: "t" | "ct" | string | null;
  itemDefIndex?: number | null;
  itemName?: string | null;
  paintKit?: number | null;
  finishName?: string | null;
  seed?: number | null;
  wear?: number | null;
  quality?: number | null;
  stattrakCounter?: number | null;
  originalOwnerSteamId?: string | null;
  itemAccountId?: number | null;
  itemId?: string | null;
  customName?: string | null;
  stickers?: StickerEvidence[];
  charms?: CharmEvidence[];
  inspectCommand?: string | null;
  inspectUrl?: string | null;
}

export interface StickerEvidence {
  slot: number;
  stickerId: number;
  wear: number;
  offsetX: number;
  offsetY: number;
  scale?: number | null;
  rotation?: number | null;
}

export interface CharmEvidence {
  slot: number;
  charmId: number;
  offsetX: number;
  offsetY: number;
  offsetZ: number;
  seed?: number | null;
  highlight?: number | null;
  stickerId?: number | null;
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
  details?: PlayerDetails | null;
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
  subtickMode: SubtickMode;
  maxRoundSeconds: number;
  exportVoice: boolean;
  exportCosmetics: boolean;
  exportStickers: boolean;
  exportCharms: boolean;
  includeSuspicious: boolean;
}

export interface LocalEnvironmentSettings {
  cs2Path: string;
  demoRoots: string[];
  soundNotifications: boolean;
}

export interface Cs2InstallCandidate {
  path: string;
  gameCsgoPath: string;
  source: string;
  label: string;
}

export type EnvironmentOverallStatus = "pass" | "warning" | "error" | "unverified";
export type EnvironmentCheckStatus = EnvironmentOverallStatus | "notApplicable";
export type RuntimeVerificationStatus = "verified" | "notRunning" | "unavailable" | "unknown";

export interface EnvironmentDiagnosticCheck {
  id: string;
  group: string;
  status: EnvironmentCheckStatus;
  title: string;
  summary: string;
  expected?: string | null;
  actual?: string | null;
  evidencePath?: string | null;
  action?: string | null;
}

export type EnvironmentPluginClassification =
  | "demotracer"
  | "dependency"
  | "potentialConflict"
  | "unknown";

export interface EnvironmentPluginInfo {
  name: string;
  directory: string;
  assemblyFiles: string[];
  classification: EnvironmentPluginClassification;
  runtimeState: "loaded" | "notLoaded" | "unknown";
}

export interface EnvironmentConflict {
  ruleId: string;
  severity: "warning" | "error";
  confidence: "certain" | "high" | "medium" | "low";
  title: string;
  summary: string;
  evidencePath: string;
  affectedFeatures: string[];
}

export interface EnvironmentInstallReceipt {
  found: boolean;
  path?: string | null;
  bundleVersion?: string | null;
  manifestAbi?: number | null;
  botControllerAbi?: number | null;
  botControllerMinor?: number | null;
  botHiderApi?: number | null;
  demoTracerApi?: number | null;
  verified?: boolean | null;
  filesChecked: number;
  filesMismatched: number;
}

export interface EnvironmentDiagnosticReport {
  /** Frontend-only marker for a report restored from local storage. */
  cached?: boolean;
  checkedAtMs: number;
  requestedPath: string;
  cs2Root: string;
  gameCsgoPath: string;
  overall: EnvironmentOverallStatus;
  runtimeVerification: RuntimeVerificationStatus;
  checks: EnvironmentDiagnosticCheck[];
  plugins: EnvironmentPluginInfo[];
  conflicts: EnvironmentConflict[];
  receipt: EnvironmentInstallReceipt;
}

export interface ServerConfigIssue {
  path: string;
  code: string;
  message: string;
}

export interface ServerConfigValidation {
  valid: boolean;
  errors: ServerConfigIssue[];
  warnings: ServerConfigIssue[];
  unknownPaths: string[];
  hasLegacyAlign: boolean;
  hasNewSections: boolean;
}

export interface ServerConfigDocument {
  cs2Root: string;
  gameCsgoPath: string;
  configPath: string;
  source: "installed" | "example" | "builtInDefault";
  exists: boolean;
  json: string;
  normalizedJson?: string | null;
  fingerprint?: string | null;
  validation: ServerConfigValidation;
  runtimeVerified: boolean;
  reloadCommand: string;
}

export interface SaveServerConfigResult {
  document: ServerConfigDocument;
  requiresReload: boolean;
  reloadCommand: string;
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

export type TaskPhase = "decompressing" | "parsing" | "analyzing" | "exporting" | "voice" | "validating" | "complete";

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

export interface DemoScanCandidate {
  path: string;
  relativePath: string;
  fileName: string;
  sizeBytes: string;
  compressed: boolean;
  modifiedAtMs?: number | null;
}

export interface DemoFolderScan {
  root: string;
  recursive: boolean;
  limit: number;
  candidates: DemoScanCandidate[];
  truncated: boolean;
  skippedReparsePoints: number;
  warnings: string[];
}

export type BatchStatus =
  | "pending"
  | "running"
  | "stopping"
  | "paused"
  | "completed"
  | "completedWithErrors";

export type BatchItemStatus = "pending" | "running" | "completed" | "failed";
export type BatchItemPhase =
  | "queued"
  | "decompressing"
  | "parsing"
  | "analyzing"
  | "converting"
  | "voice"
  | "validating"
  | "complete"
  | "failed";

export interface BatchError {
  code: string;
  message: string;
  path?: string | null;
}

export interface BatchCalibration {
  samples: number;
  secondsPerGib: number;
  firstItemId: string;
  firstParseMs: number;
}

export interface BatchItem {
  itemId: string;
  sourcePath: string;
  relativePath: string;
  fileName: string;
  sizeBytes: string;
  modifiedAtMs?: number | null;
  status: BatchItemStatus;
  phase: BatchItemPhase;
  attempts: number;
  parseMs?: number | null;
  predictedParseMs?: number | null;
  demoSha256?: string | null;
  map?: string | null;
  serverName?: string | null;
  archiveRoot?: string | null;
  manifestPath?: string | null;
  roundsExported?: number | null;
  filesWritten?: number | null;
  error?: BatchError | null;
}

export interface BatchLedger {
  schemaVersion: number;
  batchId: string;
  revision: number;
  createdAtMs: number;
  updatedAtMs: number;
  sourceRoot: string;
  libraryRoot: string;
  settings: {
    includeSuspicious: boolean;
    fullRound: boolean;
    side: SideChoice;
    subtickMode: SubtickMode;
    maxRoundSeconds: number;
    freezePrerollSeconds: number;
    exportVoice: boolean;
    exportCosmetics: boolean;
    exportStickers: boolean;
    exportCharms: boolean;
  };
  status: BatchStatus;
  cancelRequested: boolean;
  requestedConcurrency?: number | null;
  concurrency: number;
  calibration?: BatchCalibration | null;
  items: BatchItem[];
}

export interface BatchList {
  batches: BatchLedger[];
}

export type BatchEvent =
  | { kind: "started"; batchId: string; total: number; concurrency: number }
  | { kind: "itemPhase"; batchId: string; itemId: string; phase: BatchItemPhase; parseEtaSeconds?: number | null }
  | { kind: "itemTask"; batchId: string; itemId: string; task: TaskEvent; parseEtaSeconds?: number | null }
  | { kind: "estimateUpdated"; batchId: string; parseEtaSeconds: number; samples: number }
  | {
      kind: "itemCompleted";
      batchId: string;
      itemId: string;
      archiveRoot: string;
      manifestPath: string;
      parseEtaSeconds?: number | null;
    }
  | { kind: "itemFailed"; batchId: string; itemId: string; error: BatchError; parseEtaSeconds?: number | null }
  | { kind: "paused"; batchId: string; completed: number; failed: number; pending: number }
  | { kind: "finished"; batchId: string; completed: number; failed: number };
