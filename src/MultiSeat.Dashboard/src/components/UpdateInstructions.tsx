import type { UpdateComponent } from "../api/types";
import { safeReleaseUrl } from "./updateUtils";

// Update instructions. TEXT ONLY: the dashboard never runs anything. Every command here is for
// the person at the host to read and run themselves.

const MULTISEAT_COPY = "Copy-Item 'C:\\Program Files\\MultiSeat' 'C:\\Program Files\\MultiSeat.bak-<date>' -Recurse";
const MULTISEAT_INSTALL = ".\\scripts\\install-service.ps1 -FromZip .\\multiseat-windows-x64.zip";

export function ReleaseLink({ url, children }: { url: string | null | undefined; children: string }) {
  const safe = safeReleaseUrl(url);
  if (!safe) return null;
  return (
    <a className="btn-link btn-sm update-link" href={safe} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  );
}

export function UpdateInstructions({ component }: { component: UpdateComponent }) {
  const releaseUrl = component.latest?.releaseUrl;

  if (component.id === "multiseat") {
    return (
      <div className="update-instructions">
        <p>
          Run these on the host, in an elevated PowerShell, <strong>when nobody is streaming</strong>.
          The service restarts during the update, so every seat drops.
        </p>
        <ol>
          <li>
            Download <code>multiseat-windows-x64.zip</code> from the release page and unzip it to a folder.
          </li>
          <li>
            Copy the install folder first, so you can roll back:
            <pre className="update-code">{MULTISEAT_COPY}</pre>
          </li>
          <li>
            From the unzipped folder:
            <pre className="update-code">{MULTISEAT_INSTALL}</pre>
          </li>
        </ol>
        <ul>
          <li>
            The installer keeps your <code>appsettings.json</code> and <code>appsettings.local.json</code> byte
            for byte and puts a copy in <code>C:\ProgramData\MultiSeat\config-backups\</code>. From 0.6.20 it
            also keeps a full backup of the old install folder in{" "}
            <code>C:\ProgramData\MultiSeat\install-backups\</code>.
          </li>
          <li>
            Do <strong>not</strong> run a plain <code>.\scripts\install-service.ps1</code> (a source build) over
            an install that came from a release zip. Before 0.6.20 that could mix a self-contained install with a
            framework-dependent one and leave the service unable to start ("No frameworks were found"). Update
            release installs with <code>-FromZip</code>.
          </li>
          <li>
            Do not use the System page's "Rebuild &amp; Redeploy" button to update a release install. It runs
            that same source-build path.
          </li>
        </ul>
        <ReleaseLink url={releaseUrl}>Open the release page</ReleaseLink>
      </div>
    );
  }

  if (component.id === "apollovibe") {
    return (
      <div className="update-instructions">
        <p>
          The MultiSeat installer does <strong>not</strong> update an ApolloVibe that is already installed: it
          sees <code>sunshine.exe</code> and skips. There is no update script yet; one is planned. Until it
          exists, update by hand:
        </p>
        <ol>
          <li>
            When nobody is streaming, stop the MultiSeat service and anything running from your ApolloVibe folder
            (by default <code>C:\Program Files\ApolloVibe</code>).
          </li>
          <li>
            Back up <code>sunshine.exe</code> and the <code>assets</code> folder.
          </li>
          <li>
            Download <code>apollovibe-windows-x64.zip</code> from the release page and check its SHA-256 against
            the one in the release notes.
          </li>
          <li>
            Extract it over the ApolloVibe folder. The zip holds <code>sunshine.exe</code>, <code>assets</code> and{" "}
            <code>tools</code>, and no <code>config</code> folder, so Apollo's configuration is kept.
          </li>
          <li>Start the MultiSeat service again.</li>
        </ol>
        <ReleaseLink url={releaseUrl}>Open the release page</ReleaseLink>
      </div>
    );
  }

  if (component.id === "moonlightvibe") {
    return (
      <div className="update-instructions">
        <p>
          MoonlightVibe runs on your client devices, not on this host. Update it on each device from the
          release page. The app also checks GitHub itself when it starts.
        </p>
        <ReleaseLink url={releaseUrl}>Open the release page</ReleaseLink>
      </div>
    );
  }

  return null;
}
