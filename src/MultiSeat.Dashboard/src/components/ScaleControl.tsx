import { useEffect, useState } from "react";
import type { SeatInfo } from "../api/types";
import { seats as seatsApi } from "../api/client";
import {
  SCALE_AUTO,
  SCALE_CHOICES,
  choiceForScale,
  scaleFromChoice,
  scaleSourceLabel,
} from "./scale";

interface Props {
  seat: SeatInfo;
  onUpdate: () => void;
  /** False while the seat has no working session to reconnect; the scale is then shown only. */
  canChange: boolean;
}

/**
 * Shows a seat's DPI scale and where it came from, and sets or clears the seat's own override
 * through POST /api/seats/{id}/scale (issue #70). When the scale in effect changes, the service
 * reconnects the seat's session the way a resize does, so a stream pauses briefly.
 */
export function ScaleControl({ seat, onUpdate, canChange }: Props) {
  // ?? null: treat a missing field like no override, so Auto is what the editor starts on.
  const override = seat.scaleFactorOverride ?? null;
  const [open, setOpen] = useState(false);
  const [choice, setChoice] = useState(choiceForScale(override));
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  // Clear the success note after a few seconds, as ResizeControl does.
  useEffect(() => {
    if (!done) return;
    const t = setTimeout(() => setDone(null), 5000);
    return () => clearTimeout(t);
  }, [done]);

  const requested = scaleFromChoice(choice);
  const unchanged = requested === override;

  const handleOpen = () => {
    setChoice(choiceForScale(override));
    setError(null);
    setDone(null);
    setOpen(true);
  };

  const handleApply = async () => {
    if (requested === undefined || unchanged || pending) return;
    setPending(true);
    setError(null);
    try {
      const result = await seatsApi.setScale(seat.id, requested);
      // The service answers 200 without changing anything when the seat began tearing down
      // while the request waited, so check what it reports rather than trusting the status.
      if (!result?.scaleFactorSource || (result.scaleFactorOverride ?? null) !== requested) {
        setError("The service did not apply the new scale. The seat may be tearing down.");
        onUpdate();
        return;
      }
      setDone(`Scale is now ${result.scaleFactor}% (${scaleSourceLabel(result.scaleFactorSource)}).`);
      setOpen(false);
      onUpdate();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to change scale");
    } finally {
      setPending(false);
    }
  };

  if (!open) {
    return (
      <div className="resize-control">
        <div className="control-group">
          <span className="stat-label">Scale</span>
          <span style={{ fontSize: 13 }}>
            {seat.scaleFactor}%{" "}
            <span className="text-muted">({scaleSourceLabel(seat.scaleFactorSource)})</span>
          </span>
          {seat.scaleMismatch && (
            <span className="field-error" title="The scale the session was read running at differs from the one asked for.">
              Running at {seat.appliedScaleFactor}%
            </span>
          )}
          {canChange && (
            <button className="btn-sm" onClick={handleOpen}>
              Change
            </button>
          )}
        </div>
        {done && <div className="field-success">{done}</div>}
      </div>
    );
  }

  return (
    <div className="resize-control">
      <span className="stat-label">Scale</span>
      <select
        aria-label="Scale"
        value={choice}
        onChange={(e) => setChoice(e.target.value)}
        disabled={pending}
      >
        <option value={SCALE_AUTO}>Auto (clear override)</option>
        {SCALE_CHOICES.map((s) => (
          <option key={s} value={String(s)}>
            {s}%
          </option>
        ))}
      </select>
      <div className="resize-actions">
        <button
          className="btn-sm"
          onClick={handleApply}
          disabled={pending || requested === undefined || unchanged}
          title={unchanged ? "The seat already has this setting" : undefined}
        >
          {pending ? "Applying..." : "Apply"}
        </button>
        <button className="btn-ghost btn-sm" onClick={() => setOpen(false)} disabled={pending}>
          Cancel
        </button>
      </div>
      {pending && (
        <div className="text-muted" style={{ fontSize: 12 }}>
          If the scale in effect changes, the seat's session reconnects at the new scale. A live
          stream pauses for a few seconds. If Windows keeps the old scale through the reconnect,
          the session is created again, and programs running in it are closed.
        </div>
      )}
      {error && <div className="error-banner">{error}</div>}
      <p className="field-hint">
        Now {seat.scaleFactor}% ({scaleSourceLabel(seat.scaleFactorSource)}). Auto removes this
        seat's override, so the host default applies, or a scale picked from the width when the
        host has none.
      </p>
    </div>
  );
}
