import { useState } from "react";
import {
  RESOLUTION_LIMITS,
  RESOLUTION_PRESETS,
  findPreset,
  parseDimension,
  validateResolution,
} from "./resolution";

const CUSTOM = "custom";

interface Props {
  width: string;
  height: string;
  onChange: (width: string, height: string) => void;
  disabled?: boolean;
}

/**
 * A preset drop-down with a "Custom" entry that reveals width and height fields.
 * The parent owns the values, as text, so a half-typed number is not rewritten under the user.
 */
export function ResolutionPicker({ width, height, onChange, disabled }: Props) {
  // Custom mode is explicit state rather than "no preset matches": a user who picks Custom and
  // then types a preset's size should keep seeing the fields they are typing in.
  const [custom, setCustom] = useState(
    () => findPreset(parseDimension(width), parseDimension(height)) === undefined
  );

  const preset = custom ? undefined : findPreset(parseDimension(width), parseDimension(height));
  const selected = preset ? preset.label : CUSTOM;
  const error = custom ? validateResolution(width, height) : null;

  const handleSelect = (value: string) => {
    if (value === CUSTOM) {
      setCustom(true);
      return;
    }
    const p = RESOLUTION_PRESETS.find((r) => r.label === value);
    if (!p) return;
    setCustom(false);
    onChange(String(p.w), String(p.h));
  };

  return (
    <div className="resolution-picker">
      <select
        aria-label="Resolution preset"
        value={selected}
        onChange={(e) => handleSelect(e.target.value)}
        disabled={disabled}
      >
        {RESOLUTION_PRESETS.map((r) => (
          <option key={r.label} value={r.label}>
            {r.label} ({r.w}x{r.h})
          </option>
        ))}
        <option value={CUSTOM}>Custom...</option>
      </select>

      {custom && (
        <div className="resolution-custom">
          <input
            type="number"
            inputMode="numeric"
            aria-label="Width"
            placeholder="Width"
            min={RESOLUTION_LIMITS.minWidth}
            max={RESOLUTION_LIMITS.maxWidth}
            step={1}
            value={width}
            onChange={(e) => onChange(e.target.value, height)}
            disabled={disabled}
          />
          <span className="text-muted">x</span>
          <input
            type="number"
            inputMode="numeric"
            aria-label="Height"
            placeholder="Height"
            min={RESOLUTION_LIMITS.minHeight}
            max={RESOLUTION_LIMITS.maxHeight}
            step={1}
            value={height}
            onChange={(e) => onChange(width, e.target.value)}
            disabled={disabled}
          />
        </div>
      )}

      {error && <div className="field-error">{error}</div>}
    </div>
  );
}

/** Points at the global setting that follows each client's size instead of a fixed one. */
export function FollowClientHint() {
  return (
    <p className="field-hint">
      To match each Moonlight client's resolution automatically instead, set{" "}
      <code>MultiSeat:FollowClientResolution</code> to <code>true</code> in the service's{" "}
      <code>appsettings.local.json</code>. It is off by default, applies to every seat, and briefly
      interrupts the stream when a client connects at a new size.
    </p>
  );
}
