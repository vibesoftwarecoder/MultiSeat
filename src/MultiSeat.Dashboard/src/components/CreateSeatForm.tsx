import { useState } from "react";
import type { AccountInfo, NvencQualityPreset } from "../api/types";
import { seats as seatsApi } from "../api/client";
import { parseDimension, validateResolution } from "./resolution";
import { FollowClientHint, ResolutionPicker } from "./ResolutionPicker";

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
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const resolutionError = validateResolution(width, height);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!accountName || resolutionError) return;

    setCreating(true);
    setError(null);
    try {
      await seatsApi.create({
        accountName,
        width: parseDimension(width),
        height: parseDimension(height),
        fps,
        nvencPreset,
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
        <select value={accountName} onChange={(e) => setAccountName(e.target.value)}>
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

      {error && <div className="error-banner">{error}</div>}

      <button type="submit" disabled={creating || !accountName || resolutionError !== null}>
        {creating ? "Provisioning..." : "Provision Seat"}
      </button>
    </form>
  );
}
