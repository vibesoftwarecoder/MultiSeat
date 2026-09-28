// Shared by the New Seat form and the seat card's live resize (issue #70).

export interface ResolutionPreset {
  label: string;
  w: number;
  h: number;
}

export const RESOLUTION_PRESETS: ResolutionPreset[] = [
  { label: "720p", w: 1280, h: 720 },
  { label: "1080p", w: 1920, h: 1080 },
  { label: "1440p", w: 2560, h: 1440 },
  { label: "4K", w: 3840, h: 2160 },
  { label: "Ally X", w: 1920, h: 1200 },
  { label: "Deck", w: 1280, h: 800 },
];

// The service accepts a wider range for a live resize (up to 8192 in RdpGeometry.IsValid), but
// SeatRequest clamps silently to these limits, and an auto-start seat is re-created from its saved
// size through SeatRequest after a restart. A size outside these limits would therefore shrink on
// the next boot without a word, so both controls use the narrower range.
export const RESOLUTION_LIMITS = {
  minWidth: 640,
  maxWidth: 7680,
  minHeight: 480,
  maxHeight: 4320,
} as const;

/** Parse a dimension typed by the user. Returns NaN unless it is a plain whole number. */
export function parseDimension(text: string): number {
  const trimmed = text.trim();
  if (!/^\d+$/.test(trimmed)) return NaN;
  return Number(trimmed);
}

/** Returns a message describing what is wrong with this size, or null when it is usable. */
export function validateResolution(widthText: string, heightText: string): string | null {
  const { minWidth, maxWidth, minHeight, maxHeight } = RESOLUTION_LIMITS;
  const width = parseDimension(widthText);
  const height = parseDimension(heightText);

  if (!Number.isInteger(width) || width < minWidth || width > maxWidth)
    return `Width must be a whole number from ${minWidth} to ${maxWidth}.`;
  if (!Number.isInteger(height) || height < minHeight || height > maxHeight)
    return `Height must be a whole number from ${minHeight} to ${maxHeight}.`;
  return null;
}

/** The preset matching this exact size, if there is one. */
export function findPreset(width: number, height: number): ResolutionPreset | undefined {
  return RESOLUTION_PRESETS.find((p) => p.w === width && p.h === height);
}
