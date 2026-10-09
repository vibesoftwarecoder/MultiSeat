import { BrowserRouter, Routes, Route, NavLink } from "react-router-dom";
import { SeatsPage } from "./pages/SeatsPage";
import { AccountsPage } from "./pages/AccountsPage";
import { SystemPage } from "./pages/SystemPage";
import { InputPage } from "./pages/InputPage";
import { SettingsPage } from "./pages/SettingsPage";
import { TipsPage } from "./pages/TipsPage";
import { UpdatesProvider, useUpdatesContext } from "./hooks/UpdatesContext";
import { UpdateBanner } from "./components/UpdateBanner";
import { UpdateOffNotice } from "./components/UpdateOffNotice";
import { visibleAnnouncements } from "./components/updateUtils";

// A small dot after "System" while any update notice is announced and not dismissed.
function SystemNavDot() {
  const updates = useUpdatesContext();
  if (!updates || visibleAnnouncements(updates.data, updates.dismissed).length === 0) return null;
  return <span className="nav-dot" role="img" aria-label="Update notice" />;
}

export default function App() {
  return (
    <BrowserRouter>
      <UpdatesProvider>
      <div className="app-layout">
        <nav className="sidebar">
          <div className="sidebar-brand">
            <span className="brand-icon">T</span>
            <span className="brand-text">MultiSeat</span>
          </div>

          <div className="nav-links">
            <NavLink to="/" end>
              Seats
            </NavLink>
            <NavLink to="/input">
              Input
            </NavLink>
            <NavLink to="/accounts">
              Accounts
            </NavLink>
            <NavLink to="/system">
              System
              <SystemNavDot />
            </NavLink>
            <NavLink to="/settings">
              Settings
            </NavLink>
            <NavLink to="/tips">
              Tips
            </NavLink>
          </div>
        </nav>

        <main className="main-content">
          <UpdateBanner />
          <UpdateOffNotice />
          <Routes>
            <Route path="/" element={<SeatsPage />} />
            <Route path="/input" element={<InputPage />} />
            <Route path="/accounts" element={<AccountsPage />} />
            <Route path="/system" element={<SystemPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/tips" element={<TipsPage />} />
          </Routes>
        </main>
      </div>
      </UpdatesProvider>
    </BrowserRouter>
  );
}
