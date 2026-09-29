/**
 * The host-wide refresh-rate choices (issue #74). One DWM interval caps every seat on the host,
 * and only the intervals measured under a real game-like load are offered. The service refuses
 * anything else, so this list and DwmFrameIntervalSetting.AllowedIntervalsMs must agree.
 */
export interface RefreshRateChoice {
  intervalMs: number;
  label: string;
}

export const REFRESH_RATE_CHOICES: RefreshRateChoice[] = [
  { intervalMs: 33, label: "30 fps (33ms)" },
  { intervalMs: 16, label: "62 fps (16ms, the default before this release)" },
  { intervalMs: 8, label: "125 fps (8ms, new default)" },
  { intervalMs: 6, label: "166 fps (6ms)" },
];

/** The rate an interval composes at: 1000/interval, truncated, as the service computes it. */
export function hzForInterval(intervalMs: number): number {
  // Below 2 Windows ignores the value and composes at its own ~32 fps default.
  return intervalMs < 2 ? 32 : Math.floor(1000 / intervalMs);
}

/** A label for any interval, including one set by hand in the config file. */
export function refreshRateLabel(intervalMs: number): string {
  const known = REFRESH_RATE_CHOICES.find((c) => c.intervalMs === intervalMs);
  return known ? known.label : `${hzForInterval(intervalMs)} fps (${intervalMs}ms, set in config)`;
}
