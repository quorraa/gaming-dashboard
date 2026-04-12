const state = {
  socket: null,
  reconnectTimer: null,
  fitFrame: null,
  latestSnapshot: null,
  settings: null,
  settingsSaveTimer: null,
  spotifyProgressTimer: null,
  spotifySeekTimer: null,
  spotifyVolumeTimer: null,
  audioRows: 1,
  audioOptimistic: new Map(),
  audioDraggingSessionId: null,
  audioInteractionHoldUntil: 0,
  manualFullscreen: false,
  layoutMode: "default",
  settingsOpen: false,
  layoutEditorProfile: null,
  layoutEditing: false,
  layoutDrag: null,
  layoutPreview: null
};

const elements = {
  viewport: document.getElementById("dashboard-viewport"),
  dashboard: document.getElementById("dashboard"),
  fullscreenToggle: document.getElementById("fullscreen-toggle"),
  settingsToggle: document.getElementById("settings-toggle"),
  layoutToggle: document.getElementById("layout-toggle"),
  settingsBackdrop: document.getElementById("settings-backdrop"),
  settingsDrawer: document.getElementById("settings-drawer"),
  settingsClose: document.getElementById("settings-close"),
  settingsPanels: document.getElementById("settings-panels"),
  settingsAudioApps: document.getElementById("settings-audio-apps"),
  settingsLayoutProfile: document.getElementById("settings-layout-profile"),
  settingsLayoutColumns: document.getElementById("settings-layout-columns"),
  settingsLayoutRows: document.getElementById("settings-layout-rows"),
  settingsResetLayout: document.getElementById("settings-reset-layout"),
  settingsIncludeSystem: document.getElementById("settings-include-system"),
  settingsMaxSessions: document.getElementById("settings-max-sessions"),
  settingsMaxSessionsValue: document.getElementById("settings-max-sessions-value"),
  settingsClearAudio: document.getElementById("settings-clear-audio"),
  settingsSaveDiscord: document.getElementById("settings-save-discord"),
  settingsDiscordEnabled: document.getElementById("settings-discord-enabled"),
  settingsDiscordRelayUrl: document.getElementById("settings-discord-relay-url"),
  settingsDiscordApiKey: document.getElementById("settings-discord-api-key"),
  settingsDiscordApiKeyHint: document.getElementById("settings-discord-api-key-hint"),
  settingsDiscordGuild: document.getElementById("settings-discord-guild"),
  settingsDiscordMessages: document.getElementById("settings-discord-messages"),
  settingsDiscordVoice: document.getElementById("settings-discord-voice"),
  settingsDiscordTrackedUser: document.getElementById("settings-discord-tracked-user"),
  settingsDiscordLatestCount: document.getElementById("settings-discord-latest-count"),
  settingsDiscordFavorites: document.getElementById("settings-discord-favorites"),
  settingsSaveSpotify: document.getElementById("settings-save-spotify"),
  settingsConnectSpotify: document.getElementById("settings-connect-spotify"),
  settingsDisconnectSpotify: document.getElementById("settings-disconnect-spotify"),
  settingsSpotifyEnabled: document.getElementById("settings-spotify-enabled"),
  settingsSpotifyClientId: document.getElementById("settings-spotify-client-id"),
  settingsSpotifyRedirectUri: document.getElementById("settings-spotify-redirect-uri"),
  settingsSpotifyStatus: document.getElementById("settings-spotify-status"),
  panels: Array.from(document.querySelectorAll("[data-panel]")),
  tempsGrid: document.getElementById("temps-grid"),
  tempsWarning: document.getElementById("temps-warning"),
  downloadValue: document.getElementById("download-value"),
  uploadValue: document.getElementById("upload-value"),
  pingValue: document.getElementById("ping-value"),
  jitterValue: document.getElementById("jitter-value"),
  discordPanel: document.getElementById("discord-panel"),
  discordState: document.getElementById("discord-state"),
  downloadLine: document.getElementById("download-line"),
  uploadLine: document.getElementById("upload-line"),
  trackedUser: document.getElementById("tracked-user"),
  voiceBlock: document.getElementById("voice-block"),
  favoritesBlock: document.getElementById("favorites-block"),
  messagesBlock: document.getElementById("messages-block"),
  favoritesList: document.getElementById("favorites-list"),
  voiceChannel: document.getElementById("voice-channel"),
  messagesList: document.getElementById("messages-list"),
  discordWarning: document.getElementById("discord-warning"),
  spotifyPanel: document.getElementById("spotify-panel"),
  spotifyState: document.getElementById("spotify-state"),
  spotifyCard: document.getElementById("spotify-card"),
  spotifyWarning: document.getElementById("spotify-warning"),
  audioPanel: document.querySelector(".panel-audio"),
  audioDevicePicker: document.getElementById("audio-device-picker"),
  audioDevicePickerMeasure: document.getElementById("audio-device-picker-measure"),
  audioList: document.getElementById("audio-list"),
  audioWarning: document.getElementById("audio-warning"),
  processesList: document.getElementById("processes-list"),
  systemList: document.getElementById("system-list")
};

const commandTimers = new Map();
const commandLastSentAt = new Map();

init();

async function init() {
  bindUi();

  try {
    const [snapshotResponse, settingsResponse] = await Promise.all([
      fetch("/api/snapshot", { cache: "no-store" }),
      fetch("/api/settings", { cache: "no-store" })
    ]);

    if (snapshotResponse.ok) {
      render(await snapshotResponse.json());
    }

    if (settingsResponse.ok) {
      renderSettings(await settingsResponse.json());
    }
  } catch {
  }

  connect();
}

function bindUi() {
  ensureLayoutChrome();
  window.addEventListener("resize", scheduleFit);
  window.addEventListener("orientationchange", scheduleFit);
  document.addEventListener("fullscreenchange", updateFullscreenButton);
  document.addEventListener("pointermove", onGlobalPointerMove, true);
  document.addEventListener("pointerup", onGlobalPointerRelease, true);
  document.addEventListener("pointercancel", onGlobalPointerRelease, true);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && state.settingsOpen) {
      setSettingsOpen(false);
    }
  });
  elements.fullscreenToggle.addEventListener("click", toggleFullscreen);
  elements.settingsToggle.addEventListener("click", () => setSettingsOpen(!state.settingsOpen));
  elements.layoutToggle.addEventListener("click", toggleLayoutEditing);
  elements.settingsClose.addEventListener("click", () => setSettingsOpen(false));
  elements.settingsBackdrop.addEventListener("click", () => setSettingsOpen(false));
  elements.settingsPanels.addEventListener("click", onPanelToggleClick);
  elements.settingsAudioApps.addEventListener("click", onAudioAppToggleClick);
  elements.settingsLayoutProfile.addEventListener("change", () => {
    state.layoutEditorProfile = elements.settingsLayoutProfile.value || currentLayoutProfileKey();
    renderSettings(state.settings);
  });
  elements.settingsLayoutColumns.addEventListener("input", onLayoutFieldInput);
  elements.settingsLayoutRows.addEventListener("input", onLayoutFieldInput);
  elements.settingsResetLayout.addEventListener("click", () => {
    saveSettings({
      layout: {
        profile: currentLayoutEditorProfile(),
        reset: true
      }
    });
  });
  elements.settingsIncludeSystem.addEventListener("change", () => {
    saveSettings({
      audio: {
        includeSystemSounds: elements.settingsIncludeSystem.checked
      }
    });
  });
  elements.settingsClearAudio.addEventListener("click", () => {
    saveSettings({
      audio: {
        visibleSessionMatches: []
      }
    });
  });
  elements.settingsMaxSessions.addEventListener("input", () => {
    elements.settingsMaxSessionsValue.textContent = elements.settingsMaxSessions.value;
    clearTimeout(state.settingsSaveTimer);
    state.settingsSaveTimer = setTimeout(() => {
      saveSettings({
        audio: {
          maxSessions: Number(elements.settingsMaxSessions.value)
        }
      });
    }, 120);
  });
  elements.settingsSaveDiscord.addEventListener("click", () => {
    saveSettings({
      discord: collectDiscordUpdate()
    });
  });
  elements.settingsSaveSpotify.addEventListener("click", () => {
    saveSettings({
      spotify: collectSpotifyUpdate()
    });
  });
  elements.settingsConnectSpotify.addEventListener("click", async () => {
    if (!elements.settingsSpotifyClientId.value.trim()) {
      elements.settingsSpotifyStatus.value = "Client ID required";
      return;
    }
    await saveSettings({
      spotify: collectSpotifyUpdate()
    });
    window.location.href = `/api/spotify/connect/start?returnUrl=${encodeURIComponent(location.pathname)}`;
  });
  elements.settingsDisconnectSpotify.addEventListener("click", async () => {
    await fetch("/api/spotify/disconnect", { method: "POST" });
    const settingsResponse = await fetch("/api/settings", { cache: "no-store" });
    if (settingsResponse.ok) {
      renderSettings(await settingsResponse.json());
    }
  });
  elements.audioDevicePicker.addEventListener("change", () => {
    saveSettings({
      audio: {
        selectedEndpointId: elements.audioDevicePicker.value
      }
    });
  });
  elements.spotifyPanel.addEventListener("click", onSpotifyPanelClick);
  elements.spotifyPanel.addEventListener("input", onSpotifyPanelInput);
  elements.spotifyPanel.addEventListener("change", onSpotifyPanelChange);
  updateFullscreenButton();
  syncLayoutEditingUi();
  scheduleFit();
}

function connect() {
  const protocol = location.protocol === "https:" ? "wss" : "ws";
  state.socket = new WebSocket(`${protocol}://${location.host}/ws`);
  state.socket.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    if (message.type === "snapshot") {
      render(message.payload);
    }
  });
  state.socket.addEventListener("close", () => {
    clearTimeout(state.reconnectTimer);
    state.reconnectTimer = setTimeout(connect, 1500);
  });
}

function render(snapshot) {
  state.latestSnapshot = snapshot;
  renderUi(snapshot.ui);
  renderTemps(snapshot.temps);
  renderNetwork(snapshot.network);
  renderDiscord(snapshot.discord);
  renderSpotify(snapshot.spotify);
  pruneAudioOptimisticState(snapshot.audio);
  if (!shouldFreezeAudioRender()) {
    renderAudio(snapshot.audio);
  }
  renderProcesses(snapshot.processes);
  renderSystem(snapshot.system);
  scheduleFit();
}

function renderSettings(editorState) {
  state.settings = editorState;
  const preferences = editorState?.preferences;
  if (!preferences) {
    return;
  }

  const visiblePanels = new Set((preferences.visiblePanels ?? []).map(panel => String(panel).toLowerCase()));
  elements.settingsPanels.innerHTML = (editorState.availablePanels ?? []).map(panel => `
    <button class="toggle-pill ${visiblePanels.has(panel.key) ? "is-active" : ""}" type="button" data-panel-key="${escapeHtml(panel.key)}">
      ${escapeHtml(panel.label)}
    </button>
  `).join("");

  const audioPreferences = preferences.audio ?? { includeSystemSounds: true, maxSessions: 12, visibleSessionMatches: [] };
  const selectedApps = new Set((audioPreferences.visibleSessionMatches ?? []).map(name => String(name)));
  const availableApps = uniqueStrings([...(editorState.availableAudioApps ?? []), ...(audioPreferences.visibleSessionMatches ?? [])]);
  elements.settingsAudioApps.innerHTML = availableApps.length
    ? availableApps.map(name => `
      <button class="toggle-pill ${selectedApps.has(name) ? "is-active" : ""}" type="button" data-audio-app="${escapeHtml(name)}">
        ${escapeHtml(name)}
      </button>
    `).join("")
    : `<div class="mini-card"><div class="footer-note">Open the apps you care about on Windows and they will appear here.</div></div>`;

  elements.settingsIncludeSystem.checked = Boolean(audioPreferences.includeSystemSounds);
  elements.settingsMaxSessions.value = clampNumber(audioPreferences.maxSessions, 1, Number(elements.settingsMaxSessions.max)).toString();
  elements.settingsMaxSessionsValue.textContent = elements.settingsMaxSessions.value;
  renderLayoutEditor(preferences.layout);

  const discordPreferences = preferences.discord ?? {};
  elements.settingsDiscordEnabled.checked = Boolean(discordPreferences.enabled);
  elements.settingsDiscordRelayUrl.value = discordPreferences.relayUrl ?? "";
  elements.settingsDiscordApiKey.value = "";
  elements.settingsDiscordApiKey.placeholder = discordPreferences.apiKeyHint ? `Stored: ${discordPreferences.apiKeyHint}` : "Paste relay API key";
  elements.settingsDiscordApiKeyHint.textContent = discordPreferences.apiKeyHint ? `Stored key: ${discordPreferences.apiKeyHint}` : "No relay key saved yet.";
  elements.settingsDiscordGuild.value = discordPreferences.guildId ?? "";
  elements.settingsDiscordMessages.value = discordPreferences.messagesChannelId ?? "";
  elements.settingsDiscordVoice.value = discordPreferences.voiceChannelId ?? "";
  elements.settingsDiscordTrackedUser.value = discordPreferences.trackedUserId ?? "";
  elements.settingsDiscordLatestCount.value = String(clampNumber(discordPreferences.latestMessagesCount ?? 6, 1, 20));
  elements.settingsDiscordFavorites.value = Array.isArray(discordPreferences.favoriteUserIds)
    ? discordPreferences.favoriteUserIds.join("\n")
    : "";

  const spotifyPreferences = preferences.spotify ?? {};
  elements.settingsSpotifyEnabled.checked = Boolean(spotifyPreferences.enabled);
  elements.settingsSpotifyClientId.value = spotifyPreferences.clientId ?? "";
  elements.settingsSpotifyRedirectUri.value = `${location.origin}/api/spotify/connect/callback`;
  elements.settingsSpotifyStatus.value = spotifyPreferences.isAuthorized
    ? "Connected"
    : spotifyPreferences.clientId
      ? "Ready to connect"
      : "Client ID required";
}

function renderLayoutEditor(layoutPreferences) {
  const profiles = [
    { key: "desktop", label: "Desktop" },
    { key: "tablet-landscape", label: "Tablet Landscape" },
    { key: "phone-landscape", label: "Phone Landscape" }
  ];

  const selectedProfile = currentLayoutEditorProfile();
  elements.settingsLayoutProfile.innerHTML = profiles.map(profile => `
    <option value="${profile.key}" ${profile.key === selectedProfile ? "selected" : ""}>${profile.label}</option>
  `).join("");

  const layout = getLayoutProfile(layoutPreferences, selectedProfile);
  elements.settingsLayoutColumns.value = String(layout.columns ?? 1);
  elements.settingsLayoutRows.value = String(layout.rows ?? 1);
}

function renderUi(ui) {
  const visible = new Set((ui?.visiblePanels ?? []).map(panel => String(panel).toLowerCase()));
  const allPanels = elements.panels.map(panel => panel.dataset.panel);
  const customVisibility = visible.size > 0 && visible.size !== allPanels.length;

  elements.panels.forEach(panel => {
    const key = panel.dataset.panel;
    panel.classList.toggle("is-hidden", !visible.has(key));
  });

  elements.dashboard.classList.toggle("has-custom-visibility", customVisibility);
  applyDashboardLayout(ui?.layout);
  syncLayoutChrome();
}

function applyDashboardLayout(layoutPreferences) {
  const layout = getLayoutProfile(state.layoutPreview ?? layoutPreferences, currentLayoutProfileKey());
  elements.dashboard.style.setProperty("--layout-columns", String(layout.columns));
  elements.dashboard.style.setProperty("--layout-rows", String(layout.rows));
  elements.dashboard.style.gridTemplateAreas = "none";
  elements.dashboard.style.gridTemplateColumns = `repeat(${layout.columns}, minmax(0, 1fr))`;
  elements.dashboard.style.gridTemplateRows = `repeat(${layout.rows}, minmax(0, 1fr))`;
  elements.dashboard.style.gridAutoRows = "minmax(0, 1fr)";

  const layoutsByKey = new Map((layout.panels ?? []).map(panel => [String(panel.key).toLowerCase(), panel]));
  elements.panels.forEach(panel => {
    const key = String(panel.dataset.panel).toLowerCase();
    const position = layoutsByKey.get(key) ?? { x: 1, y: 1, w: 1, h: 1 };
    panel.style.gridArea = "auto";
    panel.style.gridColumn = `${position.x} / span ${position.w}`;
    panel.style.gridRow = `${position.y} / span ${position.h}`;
  });
}

function renderTemps(temps) {
  elements.tempsGrid.innerHTML = temps.cards.map(card => `
      <article class="temp-card">
        <div class="temp-label">${escapeHtml(card.label)}</div>
        <div class="temp-main">
          <div class="temp-value severity-${card.severity}">${card.value == null ? "--" : `${Math.round(card.value)}°`}</div>
          <div class="bar-track">
            <div class="bar-fill ${card.severity === "warning" ? "warning" : card.severity === "danger" ? "danger" : ""}" style="width:${card.fillPercent}%"></div>
          </div>
        </div>
      </article>
    `).join("");
  elements.tempsWarning.textContent = temps.warning ?? "";
}

function renderNetwork(network) {
  elements.downloadValue.textContent = network.downloadMbps.toFixed(0);
  elements.uploadValue.textContent = network.uploadMbps.toFixed(0);
  elements.pingValue.textContent = network.pingMs == null ? "--" : `${network.pingMs.toFixed(0)}ms`;
  elements.jitterValue.textContent = network.jitterMs == null ? "--" : `${network.jitterMs.toFixed(0)}ms`;
  elements.downloadLine.setAttribute("points", pointsFor(network.downloadHistory));
  elements.uploadLine.setAttribute("points", pointsFor(network.uploadHistory));
}

function renderDiscord(discord) {
  elements.discordPanel.classList.toggle("is-disabled", !discord.enabled);
  document.body.classList.toggle("discord-disabled", !discord.enabled);
  const stateClass = discord.enabled
    ? (discord.connectionState === "connected" ? "connected" : "warning")
    : "disabled";
  elements.discordState.className = `panel-state discord-state state-${stateClass}`;
  elements.discordState.setAttribute("aria-label", `Discord ${discord.enabled ? discord.connectionState : "disabled"}`);

  if (!discord.enabled) {
    elements.trackedUser.innerHTML = "";
    elements.voiceChannel.innerHTML = "";
    elements.favoritesList.innerHTML = "";
    elements.messagesList.innerHTML = "";
    elements.voiceBlock.hidden = true;
    elements.favoritesBlock.hidden = true;
    elements.messagesBlock.hidden = true;
    elements.discordWarning.textContent = "";
    scheduleFit();
    return;
  }

  const trackedUserId = discord.trackedUser?.id ?? "";
  const favoriteUsers = (discord.favoriteUsers ?? []).filter(user => user.id !== trackedUserId);
  const voiceMembers = discord.voiceChannel
    ? (discord.voiceChannel.members ?? []).filter(member => member.id !== trackedUserId)
    : [];

  elements.trackedUser.innerHTML = discord.trackedUser
    ? renderDiscordIdentity({
      name: discord.trackedUser.name,
      subtitle: discord.trackedUser.activity ?? "No active rich presence",
      accent: discord.trackedUser.accent
    })
    : renderDiscordIdentity({
      name: "Tracked user",
      subtitle: "No tracked user configured.",
      accent: "muted"
    });

  elements.favoritesList.innerHTML = favoriteUsers.map(renderFavoriteRow).join("");
  elements.favoritesBlock.hidden = favoriteUsers.length === 0;

  if (discord.voiceChannel) {
    elements.voiceChannel.innerHTML = `
      <div class="discord-group-note">#${escapeHtml(discord.voiceChannel.name)}</div>
      ${voiceMembers.map(renderVoiceRow).join("")}
    `;
    elements.voiceBlock.hidden = false;
  } else {
    elements.voiceChannel.innerHTML = "";
    elements.voiceBlock.hidden = true;
  }

  elements.messagesList.innerHTML = discord.latestMessages.length
    ? discord.latestMessages.map(renderMessageRow).join("")
    : "";
  elements.messagesBlock.hidden = discord.latestMessages.length === 0;

  elements.discordWarning.textContent = discord.warning ?? "";
}

function renderSpotify(spotify) {
  clearInterval(state.spotifyProgressTimer);
  state.spotifyProgressTimer = null;

  elements.spotifyPanel.classList.toggle("is-disabled", !spotify.enabled);
  const stateClass = spotify.enabled
    ? (spotify.connectionState === "connected" ? "connected" : "warning")
    : "disabled";
  elements.spotifyState.className = `panel-state spotify-state state-${stateClass}`;
  elements.spotifyState.setAttribute("aria-label", `Spotify ${spotify.enabled ? spotify.connectionState : "disabled"}`);

  if (!spotify.enabled) {
    elements.spotifyCard.innerHTML = "";
    elements.spotifyWarning.textContent = "";
    return;
  }

  const nowPlaying = spotify.nowPlaying;
  if (!nowPlaying) {
    elements.spotifyCard.innerHTML = `
      <div class="spotify-empty">
        <div class="footer-note">${escapeHtml(spotify.warning ?? "No active Spotify playback.")}</div>
      </div>
    `;
    elements.spotifyWarning.textContent = "";
    return;
  }

  const repeatLabel = nowPlaying.repeatState === "track"
    ? "Repeat One"
    : nowPlaying.repeatState === "context"
      ? "Repeat Context"
      : "Repeat Off";

  elements.spotifyCard.innerHTML = `
    <div class="spotify-now">
      <a class="spotify-cover-link" href="${escapeHtml(nowPlaying.trackUrl || "#")}" target="_blank" rel="noreferrer noopener" ${nowPlaying.trackUrl ? "" : 'tabindex="-1" aria-disabled="true"'}>
        ${nowPlaying.coverUrl
          ? `<img class="spotify-cover" src="${escapeHtml(nowPlaying.coverUrl)}" alt="${escapeHtml(nowPlaying.title)} album art">`
          : `<div class="spotify-cover spotify-cover-fallback">♪</div>`}
      </a>
      <div class="spotify-meta">
        <a class="spotify-title-link" href="${escapeHtml(nowPlaying.trackUrl || "#")}" target="_blank" rel="noreferrer noopener" ${nowPlaying.trackUrl ? "" : 'tabindex="-1" aria-disabled="true"'}>${escapeHtml(nowPlaying.title)}</a>
        <a class="spotify-meta-link" href="${escapeHtml(nowPlaying.artistUrl || "#")}" target="_blank" rel="noreferrer noopener" ${nowPlaying.artistUrl ? "" : 'tabindex="-1" aria-disabled="true"'}>${escapeHtml(nowPlaying.artist || "Unknown artist")}</a>
        <a class="spotify-meta-link" href="${escapeHtml(nowPlaying.albumUrl || "#")}" target="_blank" rel="noreferrer noopener" ${nowPlaying.albumUrl ? "" : 'tabindex="-1" aria-disabled="true"'}>${escapeHtml(nowPlaying.album || "Unknown album")}</a>
        <div class="spotify-utility-row">
          <button class="spotify-chip ${nowPlaying.isLiked ? "is-active" : ""}" type="button" data-spotify-action="${nowPlaying.isLiked ? "unlike" : "like"}" data-item-id="${escapeHtml(nowPlaying.itemId)}">${nowPlaying.isLiked ? "♥ Liked" : "♡ Like"}</button>
          <button class="spotify-chip" type="button" data-spotify-action="copy-link" data-copy-url="${escapeHtml(nowPlaying.trackUrl)}">Copy Link</button>
        </div>
      </div>
    </div>
    <div class="spotify-controls">
      <button class="spotify-control-button" type="button" data-spotify-action="previous">⏮</button>
      <button class="spotify-control-button is-primary" type="button" data-spotify-action="${nowPlaying.isPlaying ? "pause" : "play"}">${nowPlaying.isPlaying ? "⏸" : "▶"}</button>
      <button class="spotify-control-button" type="button" data-spotify-action="next">⏭</button>
      <button class="spotify-control-button ${nowPlaying.shuffleEnabled ? "is-active" : ""}" type="button" data-spotify-action="shuffle" data-spotify-value="${nowPlaying.shuffleEnabled ? "0" : "1"}">⇄</button>
      <button class="spotify-control-button ${nowPlaying.repeatState !== "off" ? "is-active" : ""}" type="button" data-spotify-action="repeat" data-repeat-state="${escapeHtml(nextRepeatState(nowPlaying.repeatState))}" title="${escapeHtml(repeatLabel)}">${nowPlaying.repeatState === "track" ? "↻1" : "↻"}</button>
    </div>
    <div class="spotify-progress-block">
      <div class="spotify-progress-row">
        <span class="footer-note" id="spotify-progress-current">${escapeHtml(formatDuration(nowPlaying.progressMs))}</span>
        <span class="footer-note" id="spotify-progress-total">${escapeHtml(formatDuration(nowPlaying.durationMs))}</span>
      </div>
      <input class="slider spotify-progress-slider" type="range" min="0" max="${Math.max(nowPlaying.durationMs, 1)}" step="1000" value="${Math.max(0, nowPlaying.progressMs)}" data-spotify-action="seek" data-device-id="${escapeHtml(nowPlaying.deviceId)}">
    </div>
    <div class="spotify-volume-row">
      <span class="footer-note spotify-device-name">${escapeHtml(nowPlaying.deviceName || "No active device")}</span>
      <div class="spotify-volume-control">
        <input class="slider spotify-volume-slider" type="range" min="0" max="100" step="1" value="${Math.max(0, nowPlaying.volumePercent)}" data-spotify-action="volume" data-device-id="${escapeHtml(nowPlaying.deviceId)}" ${nowPlaying.supportsVolume ? "" : "disabled"}>
        <span class="metric-value small">${nowPlaying.volumePercent}%</span>
      </div>
    </div>
  `;

  elements.spotifyWarning.textContent = spotify.warning ?? "";

  if (nowPlaying.isPlaying && nowPlaying.durationMs > 0) {
    const renderStartedAt = Date.now();
    const baseProgress = nowPlaying.progressMs;
    state.spotifyProgressTimer = setInterval(() => {
      const slider = elements.spotifyCard.querySelector(".spotify-progress-slider");
      const currentNode = document.getElementById("spotify-progress-current");
      if (!slider || !currentNode) {
        clearInterval(state.spotifyProgressTimer);
        state.spotifyProgressTimer = null;
        return;
      }

      const elapsed = Date.now() - renderStartedAt;
      const nextValue = Math.min(nowPlaying.durationMs, baseProgress + elapsed);
      slider.value = String(nextValue);
      currentNode.textContent = formatDuration(nextValue);
    }, 500);
  }
}

function renderAudio(audio) {
  renderAudioDevicePicker(audio);
  elements.audioList.classList.toggle("is-grid", state.layoutMode === "tablet-landscape" || state.layoutMode === "phone-landscape");
  const sessions = (audio.sessions ?? []).map(applyOptimisticAudioState);
  state.audioRows = Math.max(1, Math.ceil((sessions.length || 0) / 2));
  document.body.classList.toggle("audio-over-4", (sessions.length || 0) > 4);

  elements.audioList.innerHTML = sessions.length
    ? sessions.map(session => `
      <div class="audio-row">
        <strong class="audio-name" title="${escapeHtml(session.detail || session.name)}">${escapeHtml(session.name)}</strong>
        <div class="slider-wrap">
          <input class="slider" type="range" min="0" max="100" step="1" value="${session.volumePercent}" data-session-id="${escapeHtml(session.id)}">
          <div class="metric-value small">${session.volumePercent}%</div>
          <button class="mute-button ${session.isMuted ? "is-muted" : ""}" data-mute-id="${escapeHtml(session.id)}" data-is-muted="${session.isMuted ? "true" : "false"}" aria-label="${session.isMuted ? "Unmute" : "Mute"}" title="${escapeHtml(`${session.isMuted ? "Unmute" : "Mute"} ${session.name}`)}">
          ${session.isMuted ? "🔇" : "🔊"}
          </button>
        </div>
      </div>
    `).join("")
    : `<div class="mini-card"><div class="footer-note">No active audio sessions.</div></div>`;

  elements.audioWarning.textContent = audio.warning ?? "";

  elements.audioList.querySelectorAll(".slider").forEach(slider => {
    slider.addEventListener("pointerdown", event => {
      state.audioDraggingSessionId = event.currentTarget.dataset.sessionId;
      state.audioInteractionHoldUntil = Date.now() + 2500;
    });
    slider.addEventListener("input", event => {
      const input = event.currentTarget;
      const sessionId = input.dataset.sessionId;
      const volumePercent = Number(input.value);
      updateOptimisticAudioState(sessionId, { volumePercent });
      state.audioInteractionHoldUntil = Date.now() + 2500;
      patchAudioRowDom(sessionId, { volumePercent });
      sendCommand({ type: "setVolume", sessionId, value: volumePercent / 100 });
    });
    slider.addEventListener("change", event => {
      finalizeSliderInteraction(event.currentTarget);
    });
  });

  elements.audioList.querySelectorAll(".mute-button").forEach(button => {
    button.addEventListener("click", event => {
      const target = event.currentTarget;
      const muteId = target.dataset.muteId;
      const nextMuted = target.dataset.isMuted !== "true";
      updateOptimisticAudioState(muteId, { isMuted: nextMuted }, 2500);
      state.audioInteractionHoldUntil = Date.now() + 2500;
      patchAudioRowDom(muteId, { isMuted: nextMuted });
      sendCommand({ type: "setMute", sessionId: muteId, value: nextMuted ? 1 : 0 });
    });
  });
}

function renderAudioDevicePicker(audio) {
  const endpoints = Array.isArray(audio?.endpoints) ? audio.endpoints : [];
  const selectedEndpointId = audio?.selectedEndpointId ?? "";
  elements.audioDevicePicker.innerHTML = endpoints.map(endpoint => `
    <option value="${escapeHtml(endpoint.id)}" ${endpoint.id === selectedEndpointId ? "selected" : ""}>
      ${escapeHtml(compactEndpointName(endpoint.name, endpoint.isDefault))}
    </option>
  `).join("");
  elements.audioDevicePicker.disabled = endpoints.length <= 1;
  elements.audioDevicePicker.title = endpoints.find(endpoint => endpoint.id === selectedEndpointId)?.name ?? "";
  syncAudioDevicePickerWidth();
}

function onPanelToggleClick(event) {
  const button = event.target.closest("[data-panel-key]");
  if (!button || !state.settings?.preferences) {
    return;
  }

  const panelKey = button.dataset.panelKey;
  const current = new Set((state.settings.preferences.visiblePanels ?? []).map(panel => String(panel).toLowerCase()));
  if (current.has(panelKey) && current.size === 1) {
    return;
  }

  current.has(panelKey) ? current.delete(panelKey) : current.add(panelKey);
  saveSettings({
    visiblePanels: Array.from(current)
  });
}

function onAudioAppToggleClick(event) {
  const button = event.target.closest("[data-audio-app]");
  if (!button || !state.settings?.preferences?.audio) {
    return;
  }

  const appName = button.dataset.audioApp;
  const current = new Set(state.settings.preferences.audio.visibleSessionMatches ?? []);
  current.has(appName) ? current.delete(appName) : current.add(appName);
  saveSettings({
    audio: {
      visibleSessionMatches: Array.from(current)
    }
  });
}

function onLayoutFieldInput(event) {
  if (!state.settings?.preferences?.layout) {
    return;
  }

  const profile = currentLayoutEditorProfile();
  const columns = clampNumber(elements.settingsLayoutColumns.value, 1, 120);
  const rows = clampNumber(elements.settingsLayoutRows.value, 1, 120);
  const root = structuredClone(state.settings?.preferences?.layout ?? state.latestSnapshot?.ui?.layout);
  const property = layoutProfileProperty(profile);
  if (root?.[property]) {
    root[property] = {
      ...root[property],
      columns,
      rows,
      panels: normalizeLayoutPanels(root[property].panels ?? [], columns, rows)
    };
  }

  saveSettings({
    layout: {
      profile,
      columns,
      rows,
      panels: root?.[property]?.panels ?? []
    }
  });
}

function ensureLayoutChrome() {
  elements.panels.forEach(panel => {
    if (panel.querySelector(".layout-shell")) {
      return;
    }

    const shell = document.createElement("div");
    shell.className = "layout-shell";
    shell.innerHTML = `
      <button class="layout-move-handle" type="button" aria-label="Move panel">Move</button>
      <button class="layout-lock-toggle" type="button" aria-label="Lock panel">Lock</button>
      <div class="layout-panel-tag"></div>
      <button class="layout-resize-handle" type="button" aria-label="Resize panel">◢</button>
    `;

    const moveHandle = shell.querySelector(".layout-move-handle");
    const lockToggle = shell.querySelector(".layout-lock-toggle");
    const resizeHandle = shell.querySelector(".layout-resize-handle");
    const key = panel.dataset.panel;
    moveHandle.addEventListener("pointerdown", event => startLayoutDrag(event, key, "move"));
    resizeHandle.addEventListener("pointerdown", event => startLayoutDrag(event, key, "resize"));
    lockToggle.addEventListener("click", event => togglePanelLock(event, key));
    panel.appendChild(shell);
  });
}

function syncLayoutChrome() {
  const layout = getLayoutProfile(state.layoutPreview ?? state.latestSnapshot?.ui?.layout ?? state.settings?.preferences?.layout, currentLayoutProfileKey());
  const byKey = new Map((layout.panels ?? []).map(panel => [String(panel.key).toLowerCase(), panel]));
  elements.panels.forEach(panel => {
    const shell = panel.querySelector(".layout-shell");
    if (!shell) {
      return;
    }

    const key = String(panel.dataset.panel).toLowerCase();
    const layoutPanel = byKey.get(key);
    const locked = Boolean(layoutPanel?.locked);
    const tag = shell.querySelector(".layout-panel-tag");
    const lockToggle = shell.querySelector(".layout-lock-toggle");
    if (tag) {
      tag.textContent = titleCase(panel.dataset.panel);
    }
    if (lockToggle) {
      lockToggle.textContent = locked ? "Locked" : "Lock";
      lockToggle.classList.toggle("is-locked", locked);
      lockToggle.setAttribute("aria-label", locked ? "Unlock panel" : "Lock panel");
    }
    panel.classList.toggle("is-layout-locked", locked);
  });
}

function toggleLayoutEditing() {
  state.layoutEditing = !state.layoutEditing;
  state.layoutPreview = null;
  state.layoutDrag = null;
  syncLayoutEditingUi();
  applyDashboardLayout(state.latestSnapshot?.ui?.layout ?? state.settings?.preferences?.layout);
  scheduleFit();
}

function syncLayoutEditingUi() {
  document.body.classList.toggle("layout-editing", state.layoutEditing);
  elements.layoutToggle.classList.toggle("is-active", state.layoutEditing);
  elements.layoutToggle.textContent = state.layoutEditing ? "Lock" : "Grid";
  elements.layoutToggle.setAttribute("aria-label", state.layoutEditing ? "Lock layout editing" : "Unlock layout editing");
}

function startLayoutDrag(event, panelKey, mode) {
  if (!state.layoutEditing) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();
  event.currentTarget.setPointerCapture?.(event.pointerId);

  const profile = currentLayoutProfileKey();
  const rootLayout = structuredClone(state.settings?.preferences?.layout ?? state.latestSnapshot?.ui?.layout);
  const layout = getLayoutProfile(rootLayout, profile);
  const panel = (layout.panels ?? []).find(item => String(item.key).toLowerCase() === String(panelKey).toLowerCase());
  if (!panel || panel.locked) {
    return;
  }

  const rect = elements.dashboard.getBoundingClientRect();
  const panelElement = elements.panels.find(item => String(item.dataset.panel).toLowerCase() === String(panelKey).toLowerCase());
  const panelRect = panelElement?.getBoundingClientRect() ?? rect;
  state.layoutPreview = rootLayout;
  state.layoutDrag = {
    panelKey,
    mode,
    profile,
    initial: { ...panel },
    columns: layout.columns,
    rows: layout.rows,
    rect,
    panelRect,
    grabOffsetX: event.clientX - panelRect.left,
    grabOffsetY: event.clientY - panelRect.top
  };
}

function onGlobalPointerMove(event) {
  if (!state.layoutDrag) {
    return;
  }

  event.preventDefault();
  const drag = state.layoutDrag;
  const cellWidth = drag.rect.width / drag.columns;
  const cellHeight = drag.rect.height / drag.rows;
  const next = { ...drag.initial };
  if (drag.mode === "move") {
    const originX = event.clientX - drag.rect.left - drag.grabOffsetX;
    const originY = event.clientY - drag.rect.top - drag.grabOffsetY;
    next.x = clampNumber(Math.round(originX / Math.max(cellWidth, 1)) + 1, 1, drag.columns - drag.initial.w + 1);
    next.y = clampNumber(Math.round(originY / Math.max(cellHeight, 1)) + 1, 1, drag.rows - drag.initial.h + 1);
  } else {
    const widthUnits = Math.round((event.clientX - drag.panelRect.left) / Math.max(cellWidth, 1));
    const heightUnits = Math.round((event.clientY - drag.panelRect.top) / Math.max(cellHeight, 1));
    next.w = clampNumber(widthUnits, 1, drag.columns - drag.initial.x + 1);
    next.h = clampNumber(heightUnits, 1, drag.rows - drag.initial.y + 1);
  }

  setLayoutPreviewPanel(drag.profile, drag.panelKey, next);
  applyDashboardLayout();
}

function togglePanelLock(event, panelKey) {
  if (!state.layoutEditing) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();
  const profile = currentLayoutProfileKey();
  const layout = getLayoutProfile(state.layoutPreview ?? state.settings?.preferences?.layout ?? state.latestSnapshot?.ui?.layout, profile);
  const panel = (layout.panels ?? []).find(item => String(item.key).toLowerCase() === String(panelKey).toLowerCase());
  if (!panel) {
    return;
  }

  saveSettings({
    layout: {
      profile,
      panels: [{
        key: panel.key,
        locked: !panel.locked
      }]
    }
  });
}

function collectDiscordUpdate() {
  const apiKey = elements.settingsDiscordApiKey.value.trim();
  const update = {
    enabled: elements.settingsDiscordEnabled.checked,
    relayUrl: elements.settingsDiscordRelayUrl.value.trim(),
    guildId: elements.settingsDiscordGuild.value.trim(),
    messagesChannelId: elements.settingsDiscordMessages.value.trim(),
    voiceChannelId: elements.settingsDiscordVoice.value.trim(),
    trackedUserId: elements.settingsDiscordTrackedUser.value.trim(),
    latestMessagesCount: clampNumber(elements.settingsDiscordLatestCount.value, 1, 20),
    favoriteUserIds: elements.settingsDiscordFavorites.value
      .split(/\r?\n|,/)
      .map(value => value.trim())
      .filter(Boolean)
  };
  if (apiKey) {
    update.apiKey = apiKey;
  }
  return update;
}

function collectSpotifyUpdate() {
  return {
    enabled: elements.settingsSpotifyEnabled.checked,
    clientId: elements.settingsSpotifyClientId.value.trim()
  };
}

function renderProcesses(processes) {
  elements.processesList.innerHTML = processes.topProcesses.map(process => `
    <div class="process-row">
      <div class="process-meta">
        <strong>${escapeHtml(process.name)}</strong>
        <span class="footer-note">${process.cpuPercent.toFixed(1)}% CPU · ${process.memoryMb.toFixed(0)} MB</span>
      </div>
      <div class="process-bar bar-track">
        <div class="bar-fill ${process.cpuPercent > 60 ? "danger" : process.cpuPercent > 25 ? "warning" : ""}" style="width:${Math.min(process.cpuPercent, 100)}%"></div>
      </div>
    </div>
  `).join("");
}

function renderSystem(system) {
  const rows = [
    ["Host", system.hostName],
    ["CPU", compactSystemValue("cpu", system.cpu)],
    ["GPU", compactSystemValue("gpu", system.gpu)],
    ["RAM", system.ram],
    ["Board", compactSystemValue("board", system.board)],
    ["OS", compactSystemValue("os", system.os)],
    ["Monitor", system.monitor],
    ["Uptime", system.uptime]
  ];

  elements.systemList.innerHTML = rows.map(([label, value]) => `
      <dt>${escapeHtml(label)}</dt>
      <dd>${escapeHtml(value)}</dd>
    `).join("");
}

function onSpotifyPanelClick(event) {
  const button = event.target.closest("[data-spotify-action]");
  if (!button) {
    return;
  }

  const action = button.dataset.spotifyAction;
  if (action === "copy-link") {
    const url = button.dataset.copyUrl || "";
    if (url && navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(url).catch(() => {});
    }
    return;
  }

  sendSpotifyCommand({
    action,
    itemId: button.dataset.itemId || null,
    deviceId: button.dataset.deviceId || state.latestSnapshot?.spotify?.nowPlaying?.deviceId || null,
    repeatState: button.dataset.repeatState || null
  });
}

function onSpotifyPanelInput(event) {
  const input = event.target.closest("[data-spotify-action]");
  if (!input) {
    return;
  }

  if (input.dataset.spotifyAction === "seek") {
    const currentNode = document.getElementById("spotify-progress-current");
    if (currentNode) {
      currentNode.textContent = formatDuration(input.value);
    }

    clearTimeout(state.spotifySeekTimer);
    state.spotifySeekTimer = setTimeout(() => {
      sendSpotifyCommand({
        action: "seek",
        deviceId: input.dataset.deviceId || state.latestSnapshot?.spotify?.nowPlaying?.deviceId || null,
        value: Number(input.value)
      });
    }, 120);
  }

  if (input.dataset.spotifyAction === "volume") {
    const valueNode = input.parentElement?.querySelector(".metric-value.small");
    if (valueNode) {
      valueNode.textContent = `${input.value}%`;
    }

    clearTimeout(state.spotifyVolumeTimer);
    state.spotifyVolumeTimer = setTimeout(() => {
      sendSpotifyCommand({
        action: "volume",
        deviceId: input.dataset.deviceId || state.latestSnapshot?.spotify?.nowPlaying?.deviceId || null,
        value: Number(input.value)
      });
    }, 90);
  }
}

function onSpotifyPanelChange(event) {
  const input = event.target.closest("[data-spotify-action]");
  if (!input) {
    return;
  }

  if (input.dataset.spotifyAction === "seek") {
    clearTimeout(state.spotifySeekTimer);
    sendSpotifyCommand({
      action: "seek",
      deviceId: input.dataset.deviceId || state.latestSnapshot?.spotify?.nowPlaying?.deviceId || null,
      value: Number(input.value)
    });
  }

  if (input.dataset.spotifyAction === "volume") {
    clearTimeout(state.spotifyVolumeTimer);
    sendSpotifyCommand({
      action: "volume",
      deviceId: input.dataset.deviceId || state.latestSnapshot?.spotify?.nowPlaying?.deviceId || null,
      value: Number(input.value)
    });
  }
}

async function sendSpotifyCommand(command) {
  try {
    const response = await fetch("/api/spotify/command", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(command)
    });

    if (!response.ok) {
      const error = await response.json().catch(() => null);
      elements.spotifyWarning.textContent = error?.error || "Spotify command failed.";
      return;
    }

    const spotify = await response.json();
    if (state.latestSnapshot) {
      state.latestSnapshot.spotify = spotify;
    }
    renderSpotify(spotify);
  } catch {
    elements.spotifyWarning.textContent = "Spotify command failed.";
  }
}

function nextRepeatState(currentState) {
  return currentState === "off"
    ? "context"
    : currentState === "context"
      ? "track"
      : "off";
}

function formatDuration(value) {
  const totalSeconds = Math.max(0, Math.floor((Number(value) || 0) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function compactSystemValue(kind, value) {
  let text = String(value ?? "").trim();
  if (!text) {
    return text;
  }

  switch (kind) {
    case "cpu":
      text = text
        .replace(/^AMD\s+/i, "")
        .replace(/^Intel\(R\)\s+/i, "")
        .replace(/\s+\d+-Core Processor$/i, "")
        .replace(/\s+Processor$/i, "")
        .replace(/\s+CPU$/i, "")
        .trim();
      break;
    case "gpu":
      text = text
        .replace(/^NVIDIA\s+/i, "")
        .replace(/^AMD\s+/i, "")
        .replace(/^Intel\(R\)\s+/i, "")
        .replace(/^GeForce\s+/i, "")
        .replace(/^Radeon\s+/i, "")
        .replace(/^Graphics\s+/i, "")
        .trim();
      break;
    case "board":
      text = text
        .replace(/\s+\([^)]*\)\s*$/i, "")
        .replace(/^Micro-Star International Co\., Ltd\.\s*/i, "")
        .replace(/^ASUSTeK COMPUTER INC\.\s*/i, "")
        .replace(/^Gigabyte Technology Co\., Ltd\.\s*/i, "")
        .trim();
      break;
    case "os":
      text = text
        .replace(/^Microsoft\s+/i, "")
        .trim();
      break;
  }

  return text.replace(/\s{2,}/g, " ").trim();
}

function compactEndpointName(value, isDefault) {
  const original = String(value ?? "").trim();
  if (!original) {
    return isDefault ? "Default" : "Audio Device";
  }

  const compacted = original
    .replace(/[()]+/g, " ")
    .replace(/\s{2,}/g, " ")
    .trim();

  if (compacted) {
    return compacted;
  }

  const fallback = original
    .replace(/[()]+/g, " ")
    .replace(/\s{2,}/g, " ")
    .trim();

  return fallback || (isDefault ? "Default" : "Audio Device");
}

function syncAudioDevicePickerWidth() {
  const select = elements.audioDevicePicker;
  const measure = elements.audioDevicePickerMeasure;
  if (!select || !measure) {
    return;
  }

  const selectedOption = select.options[select.selectedIndex];
  const text = selectedOption?.text?.trim() || "Audio Device";
  measure.textContent = text;

  const isPhoneLandscape = state.layoutMode === "phone-landscape";
  const minWidth = isPhoneLandscape ? 92 : 124;
  const maxWidth = isPhoneLandscape ? 156 : 240;
  const measuredWidth = Math.ceil(measure.getBoundingClientRect().width) + (isPhoneLandscape ? 30 : 34);
  const width = Math.max(minWidth, Math.min(maxWidth, measuredWidth));
  select.style.width = `${width}px`;
}

function renderDiscordIdentity({ name, subtitle, accent }) {
  return `
    <div class="discord-identity-card">
      <div class="discord-identity-row">
        <span class="discord-dot accent-${escapeHtml(accent)}"></span>
        <strong>${escapeHtml(name)}</strong>
      </div>
      <div class="discord-subtitle">${escapeHtml(subtitle)}</div>
    </div>
  `;
}

function renderVoiceRow(member) {
  const state = [
    member.isMuted ? "Muted" : "Live",
    member.isDeafened ? "Deaf" : "Listen"
  ].join(" · ");

  return `
    <div class="discord-line">
      <div class="discord-line-main">
        <span class="discord-dot accent-${escapeHtml(member.accent)}"></span>
        <span class="discord-name">${escapeHtml(member.name)}</span>
      </div>
      <div class="discord-line-side">${escapeHtml(state)}</div>
    </div>
  `;
}

function renderFavoriteRow(user) {
  return `
    <div class="discord-line">
      <div class="discord-line-main">
        <span class="discord-dot accent-${escapeHtml(user.accent)}"></span>
        <span class="discord-name">${escapeHtml(user.name)}</span>
      </div>
      <div class="discord-line-side">${escapeHtml(user.activity ?? user.status)}</div>
    </div>
  `;
}

function renderMessageRow(message) {
  return `
    <div class="discord-message-row">
      <div class="discord-message-top">
        <span class="discord-name">${escapeHtml(message.author)}</span>
        <span class="discord-line-side">${escapeHtml(message.relativeTime)}</span>
      </div>
      <div class="discord-message-copy">${escapeHtml(message.content)}</div>
    </div>
  `;
}

function pointsFor(values) {
  const items = values.length ? values : [0];
  const max = Math.max(...items, 1);
  return items.map((value, index) => {
    const x = items.length === 1 ? 0 : (index / (items.length - 1)) * 300;
    const y = 68 - ((value / max) * 62);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
}

function scheduleCommand(key, payload, delayMs) {
  clearTimeout(commandTimers.get(key));
  commandTimers.set(key, setTimeout(() => {
    sendCommand(payload);
    commandTimers.delete(key);
  }, delayMs));
}

function throttleCommand(key, payload, intervalMs) {
  const now = Date.now();
  const lastSentAt = commandLastSentAt.get(key) ?? 0;
  const elapsed = now - lastSentAt;
  if (elapsed >= intervalMs) {
    clearTimeout(commandTimers.get(key));
    commandTimers.delete(key);
    commandLastSentAt.set(key, now);
    sendCommand(payload);
    return;
  }

  clearTimeout(commandTimers.get(key));
  commandTimers.set(key, setTimeout(() => {
    commandLastSentAt.set(key, Date.now());
    sendCommand(payload);
    commandTimers.delete(key);
  }, intervalMs - elapsed));
}

function sendCommand(payload) {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    return;
  }

  state.socket.send(JSON.stringify(payload));
}

function shouldFreezeAudioRender() {
  return Boolean(state.audioDraggingSessionId) || Date.now() < state.audioInteractionHoldUntil;
}

function onGlobalPointerRelease(event) {
  if (state.layoutDrag) {
    finalizeLayoutDrag();
    return;
  }

  const sessionId = state.audioDraggingSessionId;
  if (!sessionId) {
    return;
  }

  const slider = event.target instanceof Element
    ? event.target.closest(".audio-row")?.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`) ?? document.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`)
    : document.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`);

  if (slider) {
    finalizeSliderInteraction(slider);
    return;
  }

  state.audioDraggingSessionId = null;
}

function finalizeLayoutDrag() {
  const drag = state.layoutDrag;
  if (!drag || !state.layoutPreview) {
    state.layoutDrag = null;
    return;
  }

  const layout = getLayoutProfile(state.layoutPreview, drag.profile);
  const panel = (layout.panels ?? []).find(item => String(item.key).toLowerCase() === String(drag.panelKey).toLowerCase());
  state.layoutDrag = null;
  if (!panel) {
    state.layoutPreview = null;
    return;
  }

  const update = {
    layout: {
      profile: drag.profile,
      columns: layout.columns,
      rows: layout.rows,
      panels: (layout.panels ?? []).map(item => ({
        key: item.key,
        x: item.x,
        y: item.y,
        w: item.w,
        h: item.h,
        locked: item.locked
      }))
    }
  };

  saveSettings(update).finally(() => {
    if (state.latestSnapshot?.ui) {
      state.latestSnapshot.ui = {
        ...state.latestSnapshot.ui,
        layout: state.settings?.preferences?.layout ?? state.latestSnapshot.ui.layout
      };
    }

    state.layoutPreview = null;
    renderSettings(state.settings);
    applyDashboardLayout();
  });
}

function finalizeSliderInteraction(slider) {
  if (!slider) {
    return;
  }

  const sessionId = slider.dataset.sessionId;
  const volumePercent = Number(slider.value);
  updateOptimisticAudioState(sessionId, { volumePercent }, 2500);
  state.audioInteractionHoldUntil = Date.now() + 2500;
  patchAudioRowDom(sessionId, { volumePercent });
  sendCommand({ type: "setVolume", sessionId, value: volumePercent / 100 });
  if (state.audioDraggingSessionId === sessionId) {
    state.audioDraggingSessionId = null;
  }
}

function updateOptimisticAudioState(sessionId, patch, ttlMs = 1200) {
  const current = state.audioOptimistic.get(sessionId) ?? {};
  state.audioOptimistic.set(sessionId, {
    ...current,
    ...patch,
    expiresAt: Date.now() + ttlMs
  });
}

function applyOptimisticAudioState(session) {
  const optimistic = state.audioOptimistic.get(session.id);
  if (!optimistic) {
    return session;
  }

  if (optimistic.expiresAt < Date.now()) {
    state.audioOptimistic.delete(session.id);
    return session;
  }

  return {
    ...session,
    volumePercent: optimistic.volumePercent ?? session.volumePercent,
    isMuted: optimistic.isMuted ?? session.isMuted
  };
}

function pruneAudioOptimisticState(audio) {
  const sessions = audio?.sessions ?? [];
  const sessionMap = new Map(sessions.map(session => [session.id, session]));
  for (const [sessionId, optimistic] of state.audioOptimistic.entries()) {
    if (optimistic.expiresAt < Date.now()) {
      state.audioOptimistic.delete(sessionId);
      continue;
    }

    const actual = sessionMap.get(sessionId);
    if (!actual) {
      continue;
    }

    const volumeMatches = optimistic.volumePercent == null || actual.volumePercent === optimistic.volumePercent;
    const muteMatches = optimistic.isMuted == null || actual.isMuted === optimistic.isMuted;
    if (volumeMatches && muteMatches && state.audioDraggingSessionId !== sessionId) {
      state.audioOptimistic.delete(sessionId);
    }
  }
}

function patchAudioRowDom(sessionId, patch) {
  const slider = elements.audioList.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`);
  if (!slider) {
    return;
  }

  if (typeof patch.volumePercent === "number") {
    slider.value = String(patch.volumePercent);
    const valueNode = slider.parentElement?.querySelector(".metric-value.small");
    if (valueNode) {
      valueNode.textContent = `${patch.volumePercent}%`;
    }
  }

  if (typeof patch.isMuted === "boolean") {
    const button = slider.parentElement?.querySelector(`.mute-button[data-mute-id="${CSS.escape(sessionId)}"]`);
    if (button) {
      button.dataset.isMuted = patch.isMuted ? "true" : "false";
      button.classList.toggle("is-muted", patch.isMuted);
      button.setAttribute("aria-label", patch.isMuted ? "Unmute" : "Mute");
      button.textContent = patch.isMuted ? "🔇" : "🔊";
    }
  }
}

async function toggleFullscreen() {
  try {
    if (document.fullscreenElement) {
      if (document.exitFullscreen) {
        await document.exitFullscreen();
      } else if (document.webkitExitFullscreen) {
        document.webkitExitFullscreen();
      }
      return;
    }

    const target = document.documentElement;
    if (target.requestFullscreen) {
      await target.requestFullscreen();
    } else if (target.webkitRequestFullscreen) {
      target.webkitRequestFullscreen();
    } else if (target.msRequestFullscreen) {
      target.msRequestFullscreen();
    }
  } catch {
  }

  if (!document.fullscreenElement) {
    state.manualFullscreen = !state.manualFullscreen;
    document.body.classList.toggle("manual-fullscreen", state.manualFullscreen);
  } else {
    state.manualFullscreen = false;
    document.body.classList.remove("manual-fullscreen");
  }

  updateFullscreenButton();
  scheduleFit();
}

function updateFullscreenButton() {
  const active = Boolean(document.fullscreenElement) || state.manualFullscreen;
  elements.fullscreenToggle.classList.toggle("is-active", active);
  elements.fullscreenToggle.textContent = active ? "⤡" : "⤢";
}

function setSettingsOpen(open) {
  state.settingsOpen = open;
  document.body.classList.toggle("settings-open", open);
  elements.settingsDrawer.setAttribute("aria-hidden", open ? "false" : "true");
  elements.settingsBackdrop.hidden = !open;
}

function scheduleFit() {
  cancelAnimationFrame(state.fitFrame);
  state.fitFrame = requestAnimationFrame(fitDashboardToViewport);
}

function fitDashboardToViewport() {
  const viewport = elements.viewport;
  const dashboard = elements.dashboard;
  if (!viewport || !dashboard) {
    return;
  }

  syncLayoutMode();
  applyDashboardLayout(state.latestSnapshot?.ui?.layout ?? state.settings?.preferences?.layout);

  if (!shouldFitDashboard()) {
    document.body.classList.remove("is-fitted");
    dashboard.style.transform = "";
    dashboard.style.width = "";
    dashboard.style.height = "";
    dashboard.style.marginLeft = "";
    return;
  }

  document.body.classList.add("is-fitted");
  dashboard.style.transform = "scale(1)";
  dashboard.style.width = "";
  dashboard.style.height = "";
  dashboard.style.marginLeft = "";

  const availableWidth = viewport.clientWidth;
  const availableHeight = viewport.clientHeight;
  const naturalWidth = dashboard.scrollWidth;
  const naturalHeight = dashboard.scrollHeight;
  if (!availableWidth || !availableHeight || !naturalWidth || !naturalHeight) {
    return;
  }

  const widthScale = availableWidth / naturalWidth;
  const scaledHeightAtFullWidth = naturalHeight * widthScale;
  const scale = scaledHeightAtFullWidth <= availableHeight
    ? Math.min(1, widthScale)
    : Math.min(1, availableHeight / naturalHeight);

  dashboard.style.width = `${naturalWidth}px`;
  dashboard.style.height = `${naturalHeight}px`;
  dashboard.style.transform = `scale(${scale})`;
  dashboard.style.marginLeft = `${Math.max(0, (availableWidth - naturalWidth * scale) / 2)}px`;
}

function shouldFitDashboard() {
  return window.matchMedia("(orientation: landscape)").matches && state.layoutMode === "phone-landscape";
}

function syncLayoutMode() {
  const width = window.innerWidth;
  const height = window.innerHeight;
  const landscape = width > height;

  let mode = "default";
  if (landscape && width <= 980) {
    mode = "phone-landscape";
  } else if (landscape) {
    mode = "tablet-landscape";
  }

  state.layoutMode = mode;
  document.body.classList.toggle("layout-phone-landscape", mode === "phone-landscape");
  document.body.classList.toggle("layout-tablet-landscape", mode === "tablet-landscape");
}

function currentLayoutProfileKey() {
  return state.layoutMode === "phone-landscape"
    ? "phone-landscape"
    : state.layoutMode === "tablet-landscape"
      ? "tablet-landscape"
      : "desktop";
}

function currentLayoutEditorProfile() {
  if (!state.layoutEditorProfile) {
    state.layoutEditorProfile = currentLayoutProfileKey();
  }

  return state.layoutEditorProfile;
}

function getLayoutProfile(layoutPreferences, profile) {
  const source = layoutPreferences ?? state.settings?.preferences?.layout ?? state.latestSnapshot?.ui?.layout;
  const fallback = {
    columns: 3,
    rows: 3,
    panels: elements.panels.map(panel => ({ key: panel.dataset.panel, x: 1, y: 1, w: 1, h: 1, locked: false }))
  };

  if (!source) {
    return fallback;
  }

  const candidate = profile === "phone-landscape"
    ? source.phoneLandscape
    : profile === "tablet-landscape"
      ? source.tabletLandscape
      : source.desktop;

  return candidate ?? fallback;
}

function layoutProfileProperty(profile) {
  return profile === "phone-landscape"
    ? "phoneLandscape"
    : profile === "tablet-landscape"
      ? "tabletLandscape"
      : "desktop";
}

function setLayoutPreviewPanel(profile, panelKey, patch) {
  const root = state.layoutPreview ?? structuredClone(state.settings?.preferences?.layout ?? state.latestSnapshot?.ui?.layout);
  if (!root) {
    return;
  }

  const property = layoutProfileProperty(profile);

  const currentProfile = structuredClone(root[property] ?? getLayoutProfile(root, profile));
  currentProfile.panels = (currentProfile.panels ?? []).map(panel =>
    String(panel.key).toLowerCase() === String(panelKey).toLowerCase()
      ? { ...panel, ...patch }
      : panel);
  const resolved = resolvePriorityLayoutPanels(currentProfile.panels, currentProfile.columns, currentProfile.rows, panelKey);
  currentProfile.panels = resolved.panels;
  currentProfile.rows = Math.max(currentProfile.rows, resolved.rows);
  root[property] = currentProfile;
  state.layoutPreview = root;

  if (state.settingsOpen && currentLayoutEditorProfile() === profile) {
    renderLayoutEditor(root);
  }
}

function normalizeLayoutPanels(panels, columns, rows, priorityKey = "") {
  const source = (Array.isArray(panels) ? panels : [])
    .map(panel => ({ ...panel }))
    .map(panel => ({
      ...panel,
      locked: Boolean(panel.locked)
    }));

  const lockedPanels = source
    .filter(panel => panel.locked)
    .sort((left, right) => (left.y - right.y) || (left.x - right.x));

  const movablePanels = source
    .filter(panel => !panel.locked)
    .sort((left, right) => {
      const leftPriority = String(left.key).toLowerCase() === String(priorityKey).toLowerCase() ? -1 : 0;
      const rightPriority = String(right.key).toLowerCase() === String(priorityKey).toLowerCase() ? -1 : 0;
      if (leftPriority !== rightPriority) {
        return leftPriority - rightPriority;
      }

      if (left.y !== right.y) {
        return left.y - right.y;
      }

      return left.x - right.x;
    });

  const placed = [];
  let requiredRows = rows;
  for (const panel of lockedPanels) {
    const normalized = {
      ...panel,
      x: clampNumber(panel.x, 1, columns),
      y: clampNumber(panel.y, 1, rows),
      w: clampNumber(panel.w, 1, columns),
      h: clampNumber(panel.h, 1, rows)
    };
    normalized.w = clampNumber(normalized.w, 1, columns - normalized.x + 1);
    normalized.h = clampNumber(normalized.h, 1, rows - normalized.y + 1);
    requiredRows = Math.max(requiredRows, normalized.y + normalized.h - 1);
    placed.push(normalized);
  }

  for (const panel of movablePanels) {
    const normalized = {
      ...panel,
      x: clampNumber(panel.x, 1, columns),
      y: clampNumber(panel.y, 1, rows),
      w: clampNumber(panel.w, 1, columns),
      h: clampNumber(panel.h, 1, rows)
    };
    normalized.w = clampNumber(normalized.w, 1, columns - normalized.x + 1);
    normalized.h = clampNumber(normalized.h, 1, rows - normalized.y + 1);

    if (!placed.some(other => panelsOverlap(other, normalized))) {
      requiredRows = Math.max(requiredRows, normalized.y + normalized.h - 1);
      placed.push(normalized);
      continue;
    }

    const relocated = findOpenLayoutSlot(normalized, placed, columns, requiredRows);
    requiredRows = Math.max(requiredRows, relocated.y + relocated.h - 1);
    placed.push(relocated);
  }

  return { panels: placed, rows: requiredRows };
}

function resolvePriorityLayoutPanels(panels, columns, rows, priorityKey) {
  const normalizedResult = normalizeLayoutPanels(panels, columns, rows, "");
  const normalized = normalizedResult.panels;
  let requiredRows = normalizedResult.rows;
  const byKey = new Map(normalized.map(panel => [String(panel.key).toLowerCase(), { ...panel }]));
  const priority = byKey.get(String(priorityKey).toLowerCase());
  if (!priority) {
    return { panels: normalized, rows: requiredRows };
  }

  const lockedAnchors = normalized.filter(panel =>
    String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase() && panel.locked);

  const anchoredPriority = lockedAnchors.some(panel => panelsOverlap(panel, priority))
    ? findOpenLayoutSlot(priority, lockedAnchors, columns, requiredRows)
    : priority;
  requiredRows = Math.max(requiredRows, anchoredPriority.y + anchoredPriority.h - 1);

  const fixedMovable = normalized.filter(panel =>
    String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase()
    && !panel.locked
    && !panelsOverlap(panel, anchoredPriority));

  const displaced = normalized.filter(panel =>
    String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase()
    && !panel.locked
    && panelsOverlap(panel, anchoredPriority));

  const occupied = [...lockedAnchors, ...fixedMovable, anchoredPriority];
  const relocated = [];
  for (const panel of displaced) {
    const next = findOpenLayoutSlot(panel, [...occupied, ...relocated], columns, requiredRows);
    requiredRows = Math.max(requiredRows, next.y + next.h - 1);
    relocated.push(next);
  }

  const resolved = new Map();
  resolved.set(String(priorityKey).toLowerCase(), anchoredPriority);
  [...lockedAnchors, ...fixedMovable, ...relocated].forEach(panel => {
    resolved.set(String(panel.key).toLowerCase(), panel);
  });

  return {
    panels: normalized.map(panel => resolved.get(String(panel.key).toLowerCase()) ?? panel),
    rows: requiredRows
  };
}

function findOpenLayoutSlot(panel, placed, columns, rows) {
  const maxY = Math.max(1, Math.max(rows, 240) - panel.h + 1);
  const maxX = Math.max(1, columns - panel.w + 1);
  const preferredY = clampNumber(panel.y, 1, maxY);
  const preferredX = clampNumber(panel.x, 1, maxX);

  for (let distance = 0; distance <= rows + columns; distance += 1) {
    for (let y = 1; y <= maxY; y += 1) {
      for (let x = 1; x <= maxX; x += 1) {
        if (Math.abs(y - preferredY) + Math.abs(x - preferredX) !== distance) {
          continue;
        }

        const candidate = { ...panel, x, y };
        if (!placed.some(other => panelsOverlap(other, candidate))) {
          return candidate;
        }
      }
    }
  }

  return { ...panel, x: preferredX, y: maxY };
}

function panelsOverlap(left, right) {
  return left.x < right.x + right.w
    && left.x + left.w > right.x
    && left.y < right.y + right.h
    && left.y + left.h > right.y;
}

async function saveSettings(update) {
  if (!update) {
    return;
  }

  const previous = state.settings ? structuredClone(state.settings) : null;
  const optimistic = mergeSettingsState(state.settings, update);
  if (optimistic) {
    renderSettings(optimistic);
  }

  try {
    const response = await fetch("/api/settings", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(update)
    });

    if (!response.ok) {
      throw new Error("Failed to save settings.");
    }

    renderSettings(await response.json());
  } catch {
    if (previous) {
      renderSettings(previous);
    }
  }
}

function mergeSettingsState(currentState, update) {
  if (!currentState?.preferences) {
    return currentState;
  }

  const next = structuredClone(currentState);
  if (Array.isArray(update.visiblePanels)) {
    next.preferences.visiblePanels = uniqueStrings(update.visiblePanels.map(value => String(value).toLowerCase()));
  }

  if (update.audio) {
    const currentAudio = next.preferences.audio ?? {};
    if (typeof update.audio.includeSystemSounds === "boolean") {
      currentAudio.includeSystemSounds = update.audio.includeSystemSounds;
    }
    if (typeof update.audio.maxSessions === "number") {
      currentAudio.maxSessions = update.audio.maxSessions;
    }
    if (Array.isArray(update.audio.visibleSessionMatches)) {
      currentAudio.visibleSessionMatches = uniqueStrings(update.audio.visibleSessionMatches);
      next.availableAudioApps = uniqueStrings([...(next.availableAudioApps ?? []), ...currentAudio.visibleSessionMatches]);
    }
    if (typeof update.audio.selectedEndpointId === "string") {
      currentAudio.selectedEndpointId = update.audio.selectedEndpointId;
    }
    next.preferences.audio = currentAudio;
  }

  if (update.discord) {
    const currentDiscord = next.preferences.discord ?? {};
    if (typeof update.discord.enabled === "boolean") {
      currentDiscord.enabled = update.discord.enabled;
    }
    if (typeof update.discord.relayUrl === "string") {
      currentDiscord.relayUrl = update.discord.relayUrl;
    }
    if (typeof update.discord.apiKey === "string") {
      currentDiscord.apiKey = "";
      currentDiscord.apiKeyHint = update.discord.apiKey ? maskSecret(update.discord.apiKey) : "";
    }
    if (typeof update.discord.guildId === "string") {
      currentDiscord.guildId = update.discord.guildId;
    }
    if (typeof update.discord.messagesChannelId === "string") {
      currentDiscord.messagesChannelId = update.discord.messagesChannelId;
    }
    if (typeof update.discord.voiceChannelId === "string") {
      currentDiscord.voiceChannelId = update.discord.voiceChannelId;
    }
    if (typeof update.discord.trackedUserId === "string") {
      currentDiscord.trackedUserId = update.discord.trackedUserId;
    }
    if (typeof update.discord.latestMessagesCount === "number") {
      currentDiscord.latestMessagesCount = update.discord.latestMessagesCount;
    }
    if (Array.isArray(update.discord.favoriteUserIds)) {
      currentDiscord.favoriteUserIds = uniqueStrings(update.discord.favoriteUserIds);
    }
    next.preferences.discord = currentDiscord;
  }

  if (update.spotify) {
    const currentSpotify = next.preferences.spotify ?? {};
    if (typeof update.spotify.enabled === "boolean") {
      currentSpotify.enabled = update.spotify.enabled;
    }
    if (typeof update.spotify.clientId === "string") {
      currentSpotify.clientId = update.spotify.clientId;
      currentSpotify.isAuthorized = false;
    }
    next.preferences.spotify = currentSpotify;
  }

  if (update.layout) {
    const currentLayout = structuredClone(next.preferences.layout ?? {});
    if (update.layout.reset) {
      delete currentLayout.desktop;
      delete currentLayout.tabletLandscape;
      delete currentLayout.phoneLandscape;
    } else {
      const profile = update.layout.profile || currentLayoutProfileKey();
      const profileKey = profile === "phone-landscape"
        ? "phoneLandscape"
        : profile === "tablet-landscape"
          ? "tabletLandscape"
          : "desktop";
      const currentProfile = structuredClone(currentLayout[profileKey] ?? { columns: 3, rows: 3, panels: [] });
      if (typeof update.layout.columns === "number") {
        currentProfile.columns = update.layout.columns;
      }
      if (typeof update.layout.rows === "number") {
        currentProfile.rows = update.layout.rows;
      }
      if (Array.isArray(update.layout.panels)) {
        const byKey = new Map((currentProfile.panels ?? []).map(panel => [panel.key, panel]));
        update.layout.panels.forEach(panel => {
          const existing = byKey.get(panel.key) ?? { key: panel.key, x: 1, y: 1, w: 1, h: 1, locked: false };
          byKey.set(panel.key, {
            ...existing,
            ...(typeof panel.x === "number" ? { x: panel.x } : {}),
            ...(typeof panel.y === "number" ? { y: panel.y } : {}),
            ...(typeof panel.w === "number" ? { w: panel.w } : {}),
            ...(typeof panel.h === "number" ? { h: panel.h } : {}),
            ...(typeof panel.locked === "boolean" ? { locked: panel.locked } : {})
          });
        });
        currentProfile.panels = Array.from(byKey.values());
      }
      currentLayout[profileKey] = currentProfile;
    }

    next.preferences.layout = currentLayout;
  }

  return next;
}

function maskSecret(value) {
  const trimmed = String(value ?? "").trim();
  if (!trimmed) {
    return "";
  }
  if (trimmed.length <= 8) {
    return "*".repeat(trimmed.length);
  }
  return `${trimmed.slice(0, 4)}...${trimmed.slice(-4)}`;
}

function uniqueStrings(values) {
  const seen = new Set();
  return values.filter(value => {
    const key = String(value);
    if (!key || seen.has(key.toLowerCase())) {
      return false;
    }
    seen.add(key.toLowerCase());
    return true;
  });
}

function clampNumber(value, min, max) {
  return Math.max(min, Math.min(max, Number(value) || min));
}

function titleCase(value) {
  return String(value ?? "")
    .split(/[\s_-]+/)
    .filter(Boolean)
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}
