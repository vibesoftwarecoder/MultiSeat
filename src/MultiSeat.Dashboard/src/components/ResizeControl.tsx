import { useEffect, useState } from "react";
import type { SeatInfo } from "../api/types";
import { seats as seatsApi } from "../api/client";
import { parseDimension, validateResolution } from "./resolution";
import { FollowClientHint, ResolutionPicker } from "./ResolutionPicker";

interface Props {
  seat: SeatInfo;
  onUpdate: () => void;
}

/**
 * Changes a live seat's resolution through POST /api/seats/{id}/resolution (issue #70).
 * The service reconnects the seat's session at the new size, so a stream pauses briefly.
 */
export function ResizeControl({ seat, onUpdate }: Props) {
  const [open, setOpen] = useState(false);
  const [width, setWidth] = useState(String(seat.width));
  const [height, setHeight] = useState(String(seat.height));
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  // Clear the success note after a few seconds.
  useEffect(() => {
    if (!done) return;
    const t = setTimeout(() => setDone(null), 5000);
    return () => clearTimeout(t);
  }, [done]);

  const validation = validateResolution(width, height);
  const unchanged =
    parseDimension(width) === seat.width && parseDimension(height) === seat.height;

  const handleOpen = () => {
    setWidth(String(seat.width));
    setHeight(String(seat.height));
    setError(null);
    setDone(null);
    setOpen(true);
  };

  const handleApply = async () => {
    if (validation || unchanged || pending) return;
    const w = parseDimension(width);
    const h = parseDimension(height);
    setPending(true);
    setError(null);
    try {
      const result = await seatsApi.setResolution(seat.id, w, h);
      // The service answers 200 without changing anything when the seat began tearing down
      // while the request waited, so check the size it reports rather than trusting the status.
      if (result?.width !== w || result?.height !== h) {
        setError("The service did not apply the new size. The seat may be tearing down.");
        onUpdate();
        return;
      }
      setDone(`Resolution is now ${w}x${h}.`);
      setOpen(false);
      onUpdate();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to change resolution");
    } finally {
      setPending(false);
    }
  };

  if (!open) {
    return (
      <div className="resize-control">
        <div className="control-group">
          <span className="stat-label">Resolution</span>
          <button className="btn-sm" onClick={handleOpen}>
            Resize
          </button>
        </div>
        {done && <div className="field-success">{done}</div>}
      </div>
    );
  }

  return (
    <div className="resize-control">
      <span className="stat-label">Resize to</span>
      {/* key: a fresh picker each time the editor opens, so its Custom mode matches the seat. */}
      <ResolutionPicker
        key={`${seat.width}x${seat.height}`}
        width={width}
        height={height}
        onChange={(w, h) => {
          setWidth(w);
          setHeight(h);
        }}
        disabled={pending}
      />
      <div className="resize-actions">
        <button
          className="btn-sm"
          onClick={handleApply}
          disabled={pending || validation !== null || unchanged}
          title={unchanged ? "The seat is already this size" : undefined}
        >
          {pending ? "Resizing..." : "Apply"}
        </button>
        <button className="btn-ghost btn-sm" onClick={() => setOpen(false)} disabled={pending}>
          Cancel
        </button>
      </div>
      {pending && (
        <div className="text-muted" style={{ fontSize: 12 }}>
          Reconnecting the seat's session at the new size. A live stream pauses for a few seconds.
        </div>
      )}
      {error && <div className="error-banner">{error}</div>}
      <FollowClientHint />
    </div>
  );
}
