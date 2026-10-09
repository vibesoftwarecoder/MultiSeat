import { Link } from "react-router-dom";
import { useUpdatesContext } from "../hooks/UpdatesContext";

/**
 * One-time notice that update checks are off. It only informs: it never turns the check on.
 * The person reads what is sent on the System page and decides there.
 * Shown only when the data loaded and the check is off, and not dismissed in this browser.
 */
export function UpdateOffNotice() {
  const updates = useUpdatesContext();
  if (!updates || !updates.data || updates.data.enabled !== false || updates.offNoticeDismissed) {
    return null;
  }
  return (
    <div className="update-banners" role="region" aria-label="Update checks are off">
      <div className="info-banner update-banner">
        <div className="update-banner-row">
          <span className="update-banner-text">
            Update checks are off. MultiSeat has not contacted the internet. You can turn them on to be told
            when a new MultiSeat, ApolloVibe or MoonlightVibe release is out.
          </span>
          <span className="update-banner-actions">
            <Link className="btn-link btn-sm update-link" to="/system#updates">
              See what is sent
            </Link>
            <button type="button" className="btn-ghost btn-sm" onClick={updates.dismissOffNotice}>
              Not now
            </button>
          </span>
        </div>
      </div>
    </div>
  );
}
