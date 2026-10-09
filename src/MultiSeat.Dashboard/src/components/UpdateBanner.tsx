import { useState } from "react";
import { useUpdatesContext } from "../hooks/UpdatesContext";
import { ReleaseLink, UpdateInstructions } from "./UpdateInstructions";
import { announcementText, visibleAnnouncements } from "./updateUtils";

/**
 * Informational update notices, one line per announced component (at most three).
 * Mounted once in App above the routes. Silent when there is no data, which is also what a
 * 401 or a failed fetch looks like. It never runs anything: it shows text and a link.
 */
export function UpdateBanner() {
  const updates = useUpdatesContext();
  const [open, setOpen] = useState<string | null>(null);

  if (!updates) return null;
  const lines = visibleAnnouncements(updates.data, updates.dismissed);
  if (lines.length === 0) return null;

  return (
    <div className="update-banners" role="region" aria-label="Update notices">
      {lines.map((c) => {
        const canInstruct = c.status === "updateAvailable";
        const expanded = open === c.id;
        return (
          <div key={c.id} className="info-banner update-banner" data-component={c.id}>
            <div className="update-banner-row">
              <span className="update-banner-text">{announcementText(c)}</span>
              <span className="update-banner-actions">
                {canInstruct ? (
                  <button
                    type="button"
                    className="btn-link btn-sm"
                    aria-expanded={expanded}
                    onClick={() => setOpen(expanded ? null : c.id)}
                  >
                    How to update
                  </button>
                ) : (
                  <ReleaseLink url={c.latest?.releaseUrl}>Release notes</ReleaseLink>
                )}
                <button
                  type="button"
                  className="btn-ghost btn-sm update-dismiss"
                  aria-label={`Dismiss the ${c.name} notice`}
                  onClick={() => c.latest && updates.dismiss(c.id, c.latest.version)}
                >
                  {"×"}
                </button>
              </span>
            </div>
            {canInstruct && expanded && <UpdateInstructions component={c} />}
          </div>
        );
      })}
    </div>
  );
}
