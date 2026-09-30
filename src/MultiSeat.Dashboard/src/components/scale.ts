// Shared by the New Seat form and the seat card's live scale control (issue #70).

import type { ScaleFactorSource } from "../api/types";

/**
 * The DPI scale factors a seat may be set to, in percent. These are the only values mstsc
 * honours for desktopscalefactor. The service refuses anything else, so this list and
 * RdpGeometry.AllowedScaleFactors must agree.
 */
export const SCALE_CHOICES: readonly number[] = [100, 125, 150, 175, 200, 250, 300, 400, 500];

/** The drop-down value meaning "no override": the host default or the width decides. */
export const SCALE_AUTO = "auto";

export function isAllowedScale(scale: number): boolean {
  return SCALE_CHOICES.includes(scale);
}

/**
 * The override a drop-down value stands for: null for Auto, or the percent.
 * Returns undefined for anything that is neither, so a caller can never send a value the
 * service would refuse.
 */
export function scaleFromChoice(value: string): number | null | undefined {
  if (value === SCALE_AUTO) return null;
  if (!/^\d+$/.test(value)) return undefined;
  const scale = Number(value);
  return isAllowedScale(scale) ? scale : undefined;
}

/** The drop-down value for an override, the reverse of scaleFromChoice. */
export function choiceForScale(scale: number | null): string {
  return scale === null ? SCALE_AUTO : String(scale);
}

export function scaleSourceLabel(source: ScaleFactorSource): string {
  switch (source) {
    case "Seat":
      return "seat override";
    case "HostDefault":
      return "host default";
    default:
      return "from width";
  }
}
