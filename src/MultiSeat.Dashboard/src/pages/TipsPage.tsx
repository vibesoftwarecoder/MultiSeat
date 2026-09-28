export function TipsPage() {
  return (
    <div>
      <div className="page-header">
        <h2>Streaming Tips</h2>
      </div>

      <Section title="Frame Generation">
        <div className="info-banner" style={{ marginBottom: 16 }}>
          Host-side frame generation (Lossless Scaling, NVIDIA DLSS FG) is <strong>broken</strong> with
          SudoVDA + Apollo — DXGI capture does not pick up generated frames. Install frame gen tools
          on the <strong>Moonlight client machine</strong> instead.
        </div>

        <TipCard
          title="Lossless Scaling (recommended)"
          badge="Client-side"
          badgeColor="success"
        >
          <p>
            Install <strong>Lossless Scaling</strong> on each Moonlight client PC (Steam, ~$7).
            Run it, set Frame Generation mode to <em>LSFG</em>, then launch Moonlight inside it.
            Works with any GPU, any game, any resolution.
          </p>
          <ul>
            <li>Set the host seat to your target base FPS (e.g. 60), enable LSFG ×2 on the client → 120 FPS output</li>
            <li>Works best at a stable base framerate — cap games with RivaTuner or in-game limiter</li>
            <li>No changes needed on the MultiSeat host</li>
          </ul>
        </TipCard>

        <TipCard
          title="DLSS Frame Generation (per-game)"
          badge="Client-side"
          badgeColor="success"
        >
          <p>
            Use <strong>DLSS Swapper</strong> to install DLSS 3.8.1 into individual games.
            DLSS 310.1 and newer break streaming — the client receives the base framerate only.
            Version 3.8.1 is the last known-good build.
          </p>
          <ul>
            <li>Download DLSS Swapper, select a game, swap to DLSS 3.8.1</li>
            <li>Enable DLSS FG inside the game's graphics settings as usual</li>
            <li>Requires an NVIDIA RTX 40-series GPU on the client</li>
            <li>Per-game install — does not affect other titles</li>
          </ul>
        </TipCard>

        <TipCard
          title="AMD Fluid Motion Frames (AFMF)"
          badge="Client-side · AMD only"
          badgeColor="warning"
        >
          <p>
            AMD GPU clients can enable <strong>AFMF</strong> via AMD Software → Gaming → AMD Fluid Motion Frames.
            Works in fullscreen (exclusive or borderless) mode.
          </p>
          <ul>
            <li>Enable in AMD Software on the client — no host changes needed</li>
            <li>Works best in fullscreen Moonlight mode</li>
            <li>Requires Radeon RX 7000 series or newer</li>
          </ul>
        </TipCard>
      </Section>

      <Section title="Network & Latency">
        <TipCard title="Wired connection">
          <p>
            Run an Ethernet cable from the host to your router, and from the router to the client
            device if possible. Wi-Fi adds jitter that causes frame drops and encoding hitches even
            at high bitrates.
          </p>
        </TipCard>

        <TipCard title="Moonlight bitrate">
          <p>
            Start at <strong>50 Mbps</strong> on a gigabit LAN. Raise to 80–150 Mbps if you see
            compression artifacts in fast-motion scenes. Lowering bitrate reduces latency on
            congested networks.
          </p>
          <ul>
            <li>Settings → Video bitrate in the Moonlight app</li>
            <li>AV1 (if supported) gives better quality per Mbps than H.265</li>
            <li>H.265 is the best fallback for older client hardware</li>
          </ul>
        </TipCard>

        <TipCard title="Resolution & FPS">
          <p>
            Each seat's resolution and FPS are set at provision time in the <strong>New Seat</strong> form,
            which offers presets and a custom size. <strong>Resize</strong> on the seat card changes the
            resolution of a running seat; its session reconnects, so a live stream pauses for a few seconds.
            The NVENC quality preset (Latency / Balanced / Quality) can be changed live from the seat card
            without reprovisioning.
          </p>
          <ul>
            <li><strong>Latency</strong> — P1 encoder, no two-pass. Best for competitive games</li>
            <li><strong>Balanced</strong> — P4, quarter-res two-pass. Good for most games (default)</li>
            <li><strong>Quality</strong> — P7, full-res two-pass. Best for cinematic / slow-paced games</li>
          </ul>
        </TipCard>
      </Section>

      <Section title="Audio & Microphone">
        <TipCard title="Game audio">
          <p>
            Each seat uses a dedicated virtual audio device (VB-CABLE or VoiceMeeter bus).
            Apollo loopback-captures from that device so only that seat's game audio is streamed.
            If audio is silent after provisioning, use <strong>Manage Services → Audio → Reset</strong>
            on the seat card.
          </p>
        </TipCard>

        <TipCard title="Microphone passthrough">
          <p>
            ApolloVibe streams the Moonlight client's microphone into the seat session via
            <strong> Steam Streaming Microphone</strong>. Games see it as a normal mic input — no
            extra configuration required.
          </p>
          <ul>
            <li>Requires the standard Moonlight client (mic packets are supported in ApolloVibe)</li>
            <li>Steam must be installed in the seat's Windows account for the audio driver</li>
            <li>If the mic is silent, restart Apollo from the seat card</li>
          </ul>
        </TipCard>
      </Section>

      <Section title="HDR">
        <div className="info-banner">
          HDR is currently <strong>blocked</strong> at the SudoVDA driver layer — virtual displays do not
          expose an HDR-capable output signal. No Apollo configuration can work around this.
          Monitor the <a href="https://github.com/VirtualDrivers/SudoVDA" target="_blank" rel="noopener noreferrer">SudoVDA GitHub</a> for
          HDR virtual display support.
        </div>
      </Section>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ marginBottom: 32 }}>
      <h3 style={{ marginBottom: 12, color: "var(--text-secondary)", fontSize: 13,
                   textTransform: "uppercase", letterSpacing: "0.08em" }}>
        {title}
      </h3>
      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
        {children}
      </div>
    </div>
  );
}

type BadgeColor = "success" | "warning" | "accent";

function TipCard({
  title,
  badge,
  badgeColor = "accent",
  children,
}: {
  title: string;
  badge?: string;
  badgeColor?: BadgeColor;
  children: React.ReactNode;
}) {
  const colors: Record<BadgeColor, { bg: string; border: string; text: string }> = {
    success: { bg: "#14532d", border: "#16a34a", text: "#4ade80" },
    warning: { bg: "#451a03", border: "#d97706", text: "#fbbf24" },
    accent:  { bg: "#1e3a5f", border: "#3b82f6", text: "#93c5fd" },
  };
  const c = colors[badgeColor];

  return (
    <div className="card">
      <div className="card-body">
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }}>
          <strong style={{ fontSize: 14 }}>{title}</strong>
          {badge && (
            <span style={{
              fontSize: 11, padding: "2px 8px", borderRadius: 10,
              background: c.bg, border: `1px solid ${c.border}`, color: c.text,
            }}>
              {badge}
            </span>
          )}
        </div>
        <div className="tips-body">
          {children}
        </div>
      </div>
    </div>
  );
}
