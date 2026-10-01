import { useEffect, useState } from "react";
import type { AccountInfo, NvencQualityPreset, SeatPreset } from "../api/types";
import { seats as seatsApi } from "../api/client";
import { parseDimension, validateResolution } from "./resolution";
import { FollowClientHint, ResolutionPicker } from "./ResolutionPicker";
import { SCALE_AUTO, SCALE_CHOICES, scaleFromChoice } from "./scale";

interface Props {
  accounts: AccountInfo[];
  onCreated: () => void;
}

export function CreateSeatForm({ accounts, onCreated }: Props) {
  const [accountName, setAccountName] = useState("");
  const [width, setWidth] = useState("1920");
  const [height, setHeight] = useState("1080");
  const [fps, setFps] = useState(60);
  const [nvencPreset, setNvencPreset] = useState<NvencQualityPreset>("Balanced");
  const [scale, setScale] = useState(SCALE_AUTO);
  // Off by default, matching the API. Switched on when the chosen account already has a saved
  // auto-start preset, so creating the seat does not quietly delete it (issue #87).
  const [autoStart, setAutoStart] = useState(false);
  const [presets, setPresets] = useState<SeatPreset[]>([]);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    seatsApi.presets().then(setPresets).catch(() => {
      // Without the list the toggle just starts off, which is the API default anyway.
    });
  }, []);

  const savedPreset = presets.find(
    (p) => p.autoStart && p.accountName.toLowerCase() === accountName.toLowerCase(),
  );

  const chooseAccount = (name: string) => {
    setAccountName(name);
    setAutoStart(
      presets.some((p) => p.autoStart && p.accountName.toLowerCase() === name.toLowerCase()),
    );
  };

  const resolutionError = validateResolution(width, height);
  // null is Auto; undefined cannot come from the drop-down, but is refused rather than sent.
  const scaleFactor = scaleFromChoice(scale);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!accountName || resolutionError || scaleFactor === undefined) return;

    setCreating(true);
    setError(null);
    try {
      await seatsApi.create({
        accountName,
        width: parseDimension(width),
        height: parseDimension(height),
        fps,
        nvencPreset,
        // Always sent, so the seat ends up exactly as the toggle shows.
        autoStart,
        // Auto sends no scaleFactor at all, so the service picks it exactly as it always has.
        ...(scaleFactor !== null && { scaleFactor }),
      });
      onCreated();
      setAccountName("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to create seat");
    } finally {
      setCreating(false);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="create-form">
      <h3>New Seat</h3>

      <label>
        Account
        <select value={accountName} onChange={(e) => chooseAccount(e.target.value)}>
          <option value="">Select account...</option>
          {accounts.map((a) => (
            <option key={a.username} value={a.username}>
              {a.username}
            </option>
          ))}
        </select>
      </label>

      <div className="form-field">
        Resolution
        <ResolutionPicker
          width={width}
          height={height}
          onChange={(w, h) => {
            setWidth(w);
            setHeight(h);
          }}
          disabled={creating}
        />
        <FollowClientHint />
      </div>

      <div className="form-field">
        Scale
        <select
          aria-label="Scale"
          value={scale}
          onChange={(e) => setScale(e.target.value)}
          disabled={creating}
        >
          <option value={SCALE_AUTO}>Auto</option>
          {SCALE_CHOICES.map((s) => (
            <option key={s} value={String(s)}>
              {s}%
            </option>
          ))}
        </select>
        <p className="field-hint">
          Auto uses the host default, or picks from the width when there is none: 100% up to 1920
          wide, 125% up to 2560, 150% up to 3200, and 200% above. Choose a value for a small,
          sharp screen such as a tablet. It can be changed later on the seat's card.
        </p>
      </div>

      <label>
        FPS
        <select value={fps} onChange={(e) => setFps(Number(e.target.value))}>
          <option value={30}>30</option>
          <option value={60}>60</option>
          <option value={90}>90</option>
          <option value={120}>120</option>
          <option value={144}>144</option>
          <option value={160}>160</option>
          <option value={240}>240</option>
        </select>
      </label>

      <label>
        Quality
        <div className="preset-toggle">
          {(["Latency", "Balanced", "Quality"] as NvencQualityPreset[]).map((p) => (
            <button
              key={p}
              type="button"
              className={`preset-btn${nvencPreset === p ? " preset-btn--active" : ""}`}
              onClick={() => setNvencPreset(p)}
            >
              {p}
            </button>
          ))}
        </div>
      </label>

      <div className="form-field">
        Auto-start
        <button
          type="button"
          className={`toggle-btn${autoStart ? " toggle-btn--on" : ""}`}
          style={{ alignSelf: "flex-start" }}
          aria-pressed={autoStart}
          onClick={() => setAutoStart(!autoStart)}
          disabled={creating}
        >
          {autoStart ? "On" : "Off"}
        </button>
        <p className="field-hint">
          Keep this seat after a restart or reboot. Seats are not saved otherwise: when the
          MultiSeat service or the PC restarts, a seat with auto-start off is gone and has to be
          created again. With it on, the seat is set up again by itself whenever the service
          starts. It can be changed later on the seat's card.
          {savedPreset && " This account already has a saved auto-start seat, so this starts on."}
        </p>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <button type="submit" disabled={creating || !accountName || resolutionError !== null || scaleFactor === undefined}>
        {creating ? "Provisioning..." : "Provision Seat"}
      </button>
    </form>
  );
}
