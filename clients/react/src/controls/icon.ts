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
  Document20Regular,
  Folder20Regular,
  ChevronRight20Regular,
  ChevronDown20Regular,
  ChevronLeft20Regular,
  ChevronUp20Regular,
  ArrowRight20Regular,
  ArrowLeft20Regular,
  Checkmark20Regular,
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
  Eye20Regular,
  Link20Regular,
  Share20Regular,
} from "@fluentui/react-icons";

// A curated common-icon map (explicit imports → tree-shakeable). The previous `import * as Icons`
// pulled the ENTIRE Fluent icon set (~16 MB). MeshWeaver layout-area icons are overwhelmingly these
// common ones; anything else falls back to text (IconView) — extend this map as needed.
const ICONS: Record<string, ComponentType<any>> = {
  add: Add20Regular, plus: Add20Regular, new: Add20Regular,
  delete: Delete20Regular, trash: Delete20Regular, remove: Delete20Regular,
  edit: Edit20Regular, pencil: Edit20Regular,
  save: Save20Regular,
  search: Search20Regular,
  settings: Settings20Regular, gear: Settings20Regular,
  home: Home20Regular,
  person: Person20Regular, user: Person20Regular, account: Person20Regular,
  document: Document20Regular, file: Document20Regular,
  folder: Folder20Regular,
  chevronright: ChevronRight20Regular, chevrondown: ChevronDown20Regular,
  chevronleft: ChevronLeft20Regular, chevronup: ChevronUp20Regular,
  arrowright: ArrowRight20Regular, arrowleft: ArrowLeft20Regular,
  checkmark: Checkmark20Regular, check: Checkmark20Regular, done: Checkmark20Regular,
  dismiss: Dismiss20Regular, close: Dismiss20Regular, cancel: Dismiss20Regular,
  info: Info20Regular, information: Info20Regular,
  warning: Warning20Regular, error: ErrorCircle20Regular,
  more: MoreHorizontal20Regular,
  open: Open20Regular, copy: Copy20Regular,
  star: Star20Regular, favorite: Star20Regular,
  heart: Heart20Regular, like: Heart20Regular,
  mail: Mail20Regular, email: Mail20Regular,
  calendar: Calendar20Regular, date: Calendar20Regular,
  clock: Clock20Regular, time: Clock20Regular,
  filter: Filter20Regular,
  download: ArrowDownload20Regular, upload: ArrowUpload20Regular,
  play: Play20Regular, run: Play20Regular, pause: Pause20Regular,
  eye: Eye20Regular, view: Eye20Regular,
  link: Link20Regular, share: Share20Regular,
};

/** Resolve a MeshWeaver icon name (e.g. "Save", "fluent:Add") to a curated Fluent React icon. */
export function resolveIconByName(name: string): ComponentType<any> | undefined {
  if (!name) return undefined;
  const key = name
    .replace(/^(fluent:|Icon)/i, "")
    .replace(/[^A-Za-z0-9]/g, "")
    .replace(/(20|24|16)?(regular|filled)$/i, "")
    .toLowerCase();
  return ICONS[key];
}
