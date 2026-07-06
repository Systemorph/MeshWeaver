import type { ComponentType } from "react";
import {
  Add20Regular,
  Delete20Regular,
  Edit20Regular,
  Save20Regular,
  Search20Regular,
  Settings20Regular,
  Home20Regular,
  Person20Regular,
  PersonAdd20Regular,
  PersonSearch20Regular,
  People20Regular,
  Document20Regular,
  DocumentText20Regular,
  Folder20Regular,
  ChevronRight20Regular,
  ChevronDown20Regular,
  ChevronLeft20Regular,
  ChevronUp20Regular,
  ArrowRight20Regular,
  ArrowLeft20Regular,
  ArrowUndo20Regular,
  ArrowMove20Regular,
  ArrowSync20Regular,
  ArrowClockwise20Regular,
  ArrowRotateClockwise20Regular,
  ArrowImport20Regular,
  Checkmark20Regular,
  CheckmarkCircle20Regular,
  Dismiss20Regular,
  Info20Regular,
  Warning20Regular,
  ErrorCircle20Regular,
  MoreHorizontal20Regular,
  Open20Regular,
  Copy20Regular,
  Star20Regular,
  Heart20Regular,
  Mail20Regular,
  Calendar20Regular,
  Clock20Regular,
  Filter20Regular,
  ArrowDownload20Regular,
  ArrowUpload20Regular,
  Play20Regular,
  Pause20Regular,
  Stop20Regular,
  Eye20Regular,
  Link20Regular,
  Share20Regular,
  Shield20Regular,
  ShieldKeyhole20Regular,
  Key20Regular,
  Sparkle20Regular,
  Database20Regular,
  Code20Regular,
  Chat20Regular,
  Send20Regular,
  Pin20Regular,
  Bookmark20Regular,
  PaintBrush20Regular,
  Lightbulb20Regular,
  History20Regular,
  Comment20Regular,
  Box20Regular,
  Bot20Regular,
  Beaker20Regular,
} from "@fluentui/react-icons";
import type { Json } from "../area/types.js";

// A curated common-icon map (explicit imports → tree-shakeable). The previous `import * as Icons`
// pulled the ENTIRE Fluent icon set (~16 MB). MeshWeaver layout-area icons are overwhelmingly these
// common ones; anything else falls back to nothing (IconView) — extend this map as needed. Keys are
// normalized (lowercase, alphanumeric-only), so a Fluent id like "ArrowSync" resolves via "arrowsync".
const ICONS: Record<string, ComponentType<any>> = {
  add: Add20Regular, plus: Add20Regular, new: Add20Regular,
  delete: Delete20Regular, trash: Delete20Regular, remove: Delete20Regular,
  edit: Edit20Regular, pencil: Edit20Regular,
  save: Save20Regular,
  search: Search20Regular,
  settings: Settings20Regular, gear: Settings20Regular,
  home: Home20Regular,
  person: Person20Regular, user: Person20Regular, account: Person20Regular,
  personadd: PersonAdd20Regular,
  personsearch: PersonSearch20Regular,
  people: People20Regular, group: People20Regular,
  document: Document20Regular, file: Document20Regular,
  documenttext: DocumentText20Regular,
  folder: Folder20Regular,
  chevronright: ChevronRight20Regular, chevrondown: ChevronDown20Regular,
  chevronleft: ChevronLeft20Regular, chevronup: ChevronUp20Regular,
  arrowright: ArrowRight20Regular, arrowleft: ArrowLeft20Regular, back: ArrowLeft20Regular,
  arrowundo: ArrowUndo20Regular, undo: ArrowUndo20Regular,
  arrowmove: ArrowMove20Regular, move: ArrowMove20Regular,
  arrowsync: ArrowSync20Regular, sync: ArrowSync20Regular,
  arrowclockwise: ArrowClockwise20Regular, refresh: ArrowClockwise20Regular, reload: ArrowClockwise20Regular,
  arrowrotateclockwise: ArrowRotateClockwise20Regular,
  arrowimport: ArrowImport20Regular, import: ArrowImport20Regular,
  arrowdownload: ArrowDownload20Regular, download: ArrowDownload20Regular,
  arrowupload: ArrowUpload20Regular, upload: ArrowUpload20Regular, export: ArrowUpload20Regular,
  checkmark: Checkmark20Regular, check: Checkmark20Regular, done: Checkmark20Regular,
  checkmarkcircle: CheckmarkCircle20Regular,
  dismiss: Dismiss20Regular, close: Dismiss20Regular, cancel: Dismiss20Regular,
  info: Info20Regular, information: Info20Regular,
  warning: Warning20Regular, error: ErrorCircle20Regular,
  more: MoreHorizontal20Regular, morehorizontal: MoreHorizontal20Regular,
  open: Open20Regular, copy: Copy20Regular,
  star: Star20Regular, favorite: Star20Regular,
  heart: Heart20Regular, like: Heart20Regular,
  mail: Mail20Regular, email: Mail20Regular,
  calendar: Calendar20Regular, date: Calendar20Regular,
  clock: Clock20Regular, time: Clock20Regular,
  filter: Filter20Regular,
  play: Play20Regular, run: Play20Regular, pause: Pause20Regular, stop: Stop20Regular,
  eye: Eye20Regular, view: Eye20Regular,
  link: Link20Regular, share: Share20Regular,
  shield: Shield20Regular,
  shieldkeyhole: ShieldKeyhole20Regular,
  key: Key20Regular,
  sparkle: Sparkle20Regular, ai: Sparkle20Regular,
  database: Database20Regular,
  code: Code20Regular,
  chat: Chat20Regular,
  send: Send20Regular,
  pin: Pin20Regular, bookmark: Bookmark20Regular,
  paintbrush: PaintBrush20Regular, paint: PaintBrush20Regular,
  lightbulb: Lightbulb20Regular, idea: Lightbulb20Regular,
  history: History20Regular,
  comment: Comment20Regular,
  box: Box20Regular,
  bot: Bot20Regular, agent: Bot20Regular,
  beaker: Beaker20Regular,
};

/**
 * Extract the icon NAME from either a bare string ("Save", "fluent:Add") or the framework's
 * FluentIcon value shape — the serialized `MeshWeaver.Domain.Icon` `{ provider, id, size, variant }`
 * that nav / group / toolbar icon props carry over the wire (bound to `NavLinkControl.Icon`,
 * `NavGroupSkin.Icon`, `IconControl.Data`, etc.). Without this the object stringified to
 * "[object Object]" and every such icon rendered blank.
 */
function iconName(value: Json): string {
  if (value == null) return "";
  if (typeof value === "string") return value;
  if (typeof value === "object") {
    const id = value.id ?? value.Id ?? value.name ?? value.Name;
    return typeof id === "string" ? id : "";
  }
  return "";
}

/** Resolve a MeshWeaver icon (a name like "Save"/"fluent:Add", or a FluentIcon `{provider,id,...}`
 *  object) to a curated Fluent React icon component; `undefined` when unmapped (renders nothing). */
export function resolveIconByName(value: Json): ComponentType<any> | undefined {
  const name = iconName(value);
  if (!name) return undefined;
  const key = name
    .replace(/^(fluent-ui:|fluent:)/i, "")
    .replace(/[^A-Za-z0-9]/g, "")
    .replace(/(10|12|16|20|24|28|32|48)?(regular|filled)$/i, "")
    .toLowerCase();
  return ICONS[key];
}
