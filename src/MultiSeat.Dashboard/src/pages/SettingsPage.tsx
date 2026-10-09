import { useState } from "react";
import { useUpdatesContext } from "../hooks/UpdatesContext";

export function SettingsPage() {
  // The installed version comes from the update data; it is simply absent when that is unavailable.
  const updates = useUpdatesContext();
  const installedVersion = updates?.data?.components.find((c) => c.id === "multiseat")?.installed?.display;
  const [apiKey, setApiKey] = useState(
    () => localStorage.getItem("multiseat-api-key") ?? ""
  );
  const [saved, setSaved] = useState(false);

  const handleSave = () => {
    if (apiKey.trim()) {
      localStorage.setItem("multiseat-api-key", apiKey.trim());
    } else {
      localStorage.removeItem("multiseat-api-key");
    }
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  return (
    <div>
      <div className="page-header">
        <h2>Settings</h2>
      </div>

      <div className="card" style={{ maxWidth: 500 }}>
        <div className="card-body">
          <h3>API Connection</h3>

          <label>
            API Key
            <input
              type="password"
              placeholder="Leave blank if API key auth is disabled"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
            />
          </label>

          <p className="text-muted" style={{ fontSize: 12 }}>
            Set in the service's appsettings.json under MultiSeat.ApiKey.
            Leave blank for development mode (no auth).
          </p>

          <button onClick={handleSave}>
            {saved ? "Saved!" : "Save"}
          </button>
        </div>
      </div>

      <div className="card" style={{ maxWidth: 500, marginTop: 16 }}>
        <div className="card-body">
          <h3>About</h3>
          <p className="text-muted" style={{ fontSize: 13 }}>
            MultiSeat is a multi-seat headless game streaming system for Windows.
            It uses OS-level multi-sessioning to run isolated game instances,
            each streamed via Apollo (Sunshine fork) to Moonlight clients.
          </p>
          <div className="stat-grid">
            {installedVersion && (
              <div className="stat-item">
                <span className="stat-label">MultiSeat Version</span>
                <span className="stat-value">{installedVersion}</span>
              </div>
            )}
            <div className="stat-item">
              <span className="stat-label">API Endpoint</span>
              <span className="stat-value">{window.location.origin}/api</span>
            </div>
            <div className="stat-item">
              <span className="stat-label">WebSocket</span>
              <span className="stat-value">{window.location.origin}/ws/seats</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
