const state = {
  socket: null,
  reconnectTimer: null,
  fitFrame: null,
  latestSnapshot: null,
  settings: null,
  settingsSaveTimer: null,
  audioRows: 1,
  audioOptimistic: new Map(),
  audioDraggingSessionId: null,
  audioInteractionHoldUntil: 0,
  manualFullscreen: false,
  layoutMode: "default",
  settingsOpen: false
};

const elements = {
  viewport: document.getElementById("dashboard-viewport"),
  dashboard: document.getElementById("dashboard"),
  fullscreenToggle: document.getElementById("fullscreen-toggle"),
  settingsToggle: document.getElementById("settings-toggle"),
  settingsBackdrop: document.getElementById("settings-backdrop"),
  settingsDrawer: document.getElementById("settings-drawer"),
  settingsClose: document.getElementById("settings-close"),
  settingsPanels: document.getElementById("settings-panels"),
  settingsAudioApps: document.getElementById("settings-audio-apps"),
  settingsIncludeSystem: document.getElementById("settings-include-system"),
  settingsMaxSessions: document.getElementById("settings-max-sessions"),
  settingsMaxSessionsValue: document.getElementById("settings-max-sessions-value"),
  settingsClearAudio: document.getElementById("settings-clear-audio"),
  settingsSaveDiscord: document.getElementById("settings-save-discord"),
  settingsDiscordEnabled: document.getElementById("settings-discord-enabled"),
  settingsDiscordToken: document.getElementById("settings-discord-token"),
  settingsDiscordTokenHint: document.getElementById("settings-discord-token-hint"),
  settingsDiscordGuild: document.getElementById("settings-discord-guild"),
  settingsDiscordMessages: document.getElementById("settings-discord-messages"),
  settingsDiscordVoice: document.getElementById("settings-discord-voice"),
  settingsDiscordTrackedUser: document.getElementById("settings-discord-tracked-user"),
  settingsDiscordLatestCount: document.getElementById("settings-discord-latest-count"),
  settingsDiscordFavorites: document.getElementById("settings-discord-favorites"),
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
  audioPanel: document.querySelector(".panel-audio"),
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
  window.addEventListener("resize", scheduleFit);
  window.addEventListener("orientationchange", scheduleFit);
  document.addEventListener("fullscreenchange", updateFullscreenButton);
  document.addEventListener("pointerup", onGlobalPointerRelease, true);
  document.addEventListener("pointercancel", onGlobalPointerRelease, true);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && state.settingsOpen) {
      setSettingsOpen(false);
    }
  });
  elements.fullscreenToggle.addEventListener("click", toggleFullscreen);
  elements.settingsToggle.addEventListener("click", () => setSettingsOpen(!state.settingsOpen));
  elements.settingsClose.addEventListener("click", () => setSettingsOpen(false));
  elements.settingsBackdrop.addEventListener("click", () => setSettingsOpen(false));
  elements.settingsPanels.addEventListener("click", onPanelToggleClick);
  elements.settingsAudioApps.addEventListener("click", onAudioAppToggleClick);
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
  updateFullscreenButton();
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

  const discordPreferences = preferences.discord ?? {};
  elements.settingsDiscordEnabled.checked = Boolean(discordPreferences.enabled);
  elements.settingsDiscordToken.value = "";
  elements.settingsDiscordToken.placeholder = discordPreferences.tokenHint ? `Stored: ${discordPreferences.tokenHint}` : "Paste bot token";
  elements.settingsDiscordTokenHint.textContent = discordPreferences.tokenHint ? `Stored token: ${discordPreferences.tokenHint}` : "No token saved yet.";
  elements.settingsDiscordGuild.value = discordPreferences.guildId ?? "";
  elements.settingsDiscordMessages.value = discordPreferences.messagesChannelId ?? "";
  elements.settingsDiscordVoice.value = discordPreferences.voiceChannelId ?? "";
  elements.settingsDiscordTrackedUser.value = discordPreferences.trackedUserId ?? "";
  elements.settingsDiscordLatestCount.value = String(clampNumber(discordPreferences.latestMessagesCount ?? 6, 1, 20));
  elements.settingsDiscordFavorites.value = Array.isArray(discordPreferences.favoriteUserIds)
    ? discordPreferences.favoriteUserIds.join("\n")
    : "";
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

function renderAudio(audio) {
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

function collectDiscordUpdate() {
  const token = elements.settingsDiscordToken.value.trim();
  const update = {
    enabled: elements.settingsDiscordEnabled.checked,
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
  if (token) {
    update.token = token;
  }
  return update;
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
    next.preferences.audio = currentAudio;
  }

  if (update.discord) {
    const currentDiscord = next.preferences.discord ?? {};
    if (typeof update.discord.enabled === "boolean") {
      currentDiscord.enabled = update.discord.enabled;
    }
    if (typeof update.discord.token === "string") {
      currentDiscord.token = "";
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

  return next;
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
