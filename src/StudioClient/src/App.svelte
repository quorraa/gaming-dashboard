<script>
  import { afterUpdate, onMount } from "svelte";

  const presets = [
    { id: "neon-grid", label: "Neon Grid" },
    { id: "signal-bloom", label: "Signal Bloom" },
    { id: "ember-circuit", label: "Ember Circuit" },
    { id: "glacier-core", label: "Glacier Core" },
    { id: "graphite-wave", label: "Graphite Wave" }
  ];

  const settingsTabs = [
    { key: "panels", label: "Panels" },
    { key: "audio", label: "Audio" },
    { key: "layout", label: "Layout" },
    { key: "discord", label: "Discord" },
    { key: "spotify", label: "Spotify" }
  ];

  let snapshot = null;
  let editorState = null;
  let localMedia = [];
  let pexelsResult = null;
  let pexelsQuery = "";
  let pexelsKind = "image";
  let pexelsWarning = "";
  let settingsOpen = false;
  let themeOpen = false;
  let viewportWidth = 1;
  let viewportHeight = 1;
  let mediaCacheReady = false;
  let socket;
  let reconnectTimer;
  let spotifyProgressTimer;
  let spotifySeekTimer;
  let spotifyVolumeTimer;
  let fitFrame;
  let fitScale = 1;
  let fitWidth = "";
  let fitHeight = "";
  let fitMarginLeft = "";
  let viewportEl;
  let dashboardEl;
  let floatingDockEl;
  let floatingDockEditEl;
  let spotifySeekPreview = null;
  let spotifyVolumePreview = null;
  let audioOptimistic = {};
  let audioDraggingSessionId = null;
  const commandTimers = new Map();
  const commandLastSentAt = new Map();
  const defaultPanelKeys = ["temps", "network", "discord", "spotify", "audio", "processes", "system"];

  let panelForm = [];
  let audioTargetForm = [];
  let audioForm = { includeSystemSounds: true, maxSessions: 12, showDeviceLabels: true };
  let settingsSection = "panels";
  let discordForm = { enabled: false, relayUrl: "", apiKey: "", guildId: "", messagesChannelId: "", voiceChannelId: "", trackedUserId: "", latestMessagesCount: 6, favoriteUserIds: "" };
  let spotifyForm = { enabled: false, clientId: "", status: "Client ID required" };
  let themeForm = { presetId: "neon-grid", pexelsApiKey: "" };
  let layoutEditing = false;
  let layoutEditorProfile = "";
  let layoutPreview = null;
  let layoutDrag = null;
  let dockDrag = null;
  let dockControlsHidden = false;
  let dockInteractionLockUntil = 0;
  let dockInteractionTimer = null;
  let resolvedDockPosition = { x: 10, y: 10 };
  let layoutColumnsValue = 12;
  let layoutRowsValue = 12;
  let panelSizeFlags = {};
  let panelView = {};
  let dashboardCssText = "";
  let dockCssText = "";

  $: preferences = editorState?.preferences ?? null;
  $: visiblePanels = new Set((preferences?.visiblePanels ?? []).map((item) => String(item).toLowerCase()));
  $: profileKey = currentProfileKey(viewportWidth, viewportHeight);
  $: layoutMode = profileKey === "desktop" ? "default" : profileKey;
  $: activeLayout = layoutPreview ?? preferences?.layout ?? snapshot?.ui?.layout;
  $: layoutProfile = getActiveLayoutProfile(activeLayout, profileKey, viewportWidth, viewportHeight);
  $: background = preferences?.theme?.background ?? { source: "none", mediaKind: "none" };
  $: themePresetId = preferences?.theme?.presetId ?? "neon-grid";
  $: spotifyNow = snapshot?.spotify?.nowPlaying ?? null;
  $: shownSpotifyProgress = spotifySeekPreview ?? spotifyNow?.progressMs ?? 0;
  $: shownSpotifyVolume = spotifyVolumePreview ?? spotifyNow?.volumePercent ?? 0;
  $: audioSessions = (snapshot?.audio?.sessions ?? []).map(applyOptimisticAudioState);
  $: audioRows = Math.max(1, Math.ceil((audioSessions.length || 0) / 2));
  $: audioInventory = editorState?.audioInventory ?? { outputDevices: [], inputDevices: [] };
  $: outputAudioDevices = audioInventory.outputDevices ?? [];
  $: inputAudioDevices = audioInventory.inputDevices ?? [];
  $: allInputsMuted = inputAudioDevices.length > 0 && inputAudioDevices.every((device) => device.isMuted);
  $: discordOutputSessions = outputAudioDevices.flatMap((device) =>
    (device.sessions ?? []).filter((session) => isDiscordSession(session)).map((session) => ({ ...session, endpointId: device.id, endpointName: device.name })));
  $: discordOutputsMuted = discordOutputSessions.length > 0 && discordOutputSessions.every((session) => session.isMuted);
  $: dashboardCssText = `${layoutProfile ? `grid-template-areas:none;grid-template-columns:repeat(${layoutProfile.columns},minmax(0,1fr));grid-template-rows:repeat(${layoutProfile.rows},minmax(0,1fr));grid-auto-rows:minmax(0,1fr);--layout-columns:${layoutProfile.columns};--layout-rows:${layoutProfile.rows};` : ""}${layoutMode === "phone-landscape" ? `transform:scale(${fitScale});width:${fitWidth};height:${fitHeight};margin-left:${fitMarginLeft};` : ""}`;
  $: dockCssText = `left:${resolvedDockPosition.x}px;top:${resolvedDockPosition.y}px;`;
  $: panelView = Object.fromEntries(defaultPanelKeys.map((key) => {
    const panel = layoutProfile?.panels?.find((item) => String(item.key).toLowerCase() === key);
    return [key, {
      visible: visiblePanels.has(key),
      style: panel ? `grid-column:${panel.x} / span ${panel.w};grid-row:${panel.y} / span ${panel.h};` : "",
      locked: !!panel?.locked,
      compact: !!panelSizeFlags[key]?.compact,
      tiny: !!panelSizeFlags[key]?.tiny
    }];
  }));
  $: if (typeof document !== "undefined") {
    document.body.dataset.themePreset = themePresetId;
  }
  $: if (typeof document !== "undefined") {
    const managedClasses = [
      "studio-client",
      "settings-open",
      "theme-open",
      "layout-phone-landscape",
      "layout-tablet-landscape",
      "discord-disabled",
      "audio-over-4",
      "is-fitted",
      "layout-editing"
    ];
    document.body.classList.remove(...managedClasses);
    document.body.classList.add("studio-client");
    if (settingsOpen) document.body.classList.add("settings-open");
    if (themeOpen) document.body.classList.add("theme-open");
    if (layoutMode === "phone-landscape") document.body.classList.add("layout-phone-landscape", "is-fitted");
    if (layoutMode === "tablet-landscape") document.body.classList.add("layout-tablet-landscape");
    if (!snapshot?.discord?.enabled) document.body.classList.add("discord-disabled");
    if (audioRows > 4) document.body.classList.add("audio-over-4");
    if (layoutEditing) document.body.classList.add("layout-editing");
  }

  onMount(() => {
    updateViewport();
    loadInitial();
    connectSocket();
    registerWorker();

    const onResize = () => updateViewport();
    const onKey = (event) => {
      if (event.key === "Escape") {
        settingsOpen = false;
        themeOpen = false;
      }
    };

    window.addEventListener("resize", onResize);
    window.addEventListener("orientationchange", onResize);
    window.addEventListener("keydown", onKey);
    window.addEventListener("pointermove", onGlobalPointerMove, true);
    window.addEventListener("pointerup", onGlobalPointerRelease, true);
    window.addEventListener("pointercancel", onGlobalPointerRelease, true);

    return () => {
      window.removeEventListener("resize", onResize);
      window.removeEventListener("orientationchange", onResize);
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("pointermove", onGlobalPointerMove, true);
      window.removeEventListener("pointerup", onGlobalPointerRelease, true);
      window.removeEventListener("pointercancel", onGlobalPointerRelease, true);
      if (socket) socket.close();
      clearTimeout(reconnectTimer);
      clearInterval(spotifyProgressTimer);
      clearTimeout(spotifySeekTimer);
      clearTimeout(spotifyVolumeTimer);
      clearTimeout(dockInteractionTimer);
      cancelAnimationFrame(fitFrame);
    };
  });

  afterUpdate(() => {
    syncLayoutMetrics();
    syncDockPlacement();
    scheduleFit();
  });

  async function loadInitial() {
    try {
      const [snapshotResponse, settingsResponse, mediaResponse] = await Promise.all([
        fetch("/api/snapshot", { cache: "no-store" }),
        fetch("/api/settings", { cache: "no-store" }),
        fetch("/api/media/library", { cache: "no-store" })
      ]);
      if (snapshotResponse.ok) snapshot = await snapshotResponse.json();
      if (settingsResponse.ok) {
        editorState = await settingsResponse.json();
        syncForms(editorState);
      }
      if (mediaResponse.ok) localMedia = await mediaResponse.json();
      hydrateFromQuery();
      syncSpotifyTimer();
    } catch {
    }
  }

  async function refreshEditorState() {
    try {
      const response = await fetch("/api/settings", { cache: "no-store" });
      if (!response.ok) return;
      editorState = await response.json();
      syncForms(editorState);
    } catch {
    }
  }

  function connectSocket() {
    const protocol = location.protocol === "https:" ? "wss" : "ws";
    socket = new WebSocket(`${protocol}://${location.host}/ws`);
    socket.addEventListener("message", (event) => {
      const message = JSON.parse(event.data);
      if (message.type === "snapshot") {
        snapshot = message.payload;
        pruneAudioOptimisticState();
        syncSpotifyTimer();
      }
    });
    socket.addEventListener("close", () => {
      clearTimeout(reconnectTimer);
      reconnectTimer = setTimeout(connectSocket, 1500);
    });
  }

  async function registerWorker() {
    if (!("serviceWorker" in navigator)) return;
    try {
      await navigator.serviceWorker.register("/studio-sw.js", { updateViaCache: "none" });
      await navigator.serviceWorker.ready;
      mediaCacheReady = true;
    } catch {
      mediaCacheReady = false;
    }
  }

  function hydrateFromQuery() {
    const query = new URLSearchParams(location.search);
    const spotifyStatus = query.get("spotify");
    if (!spotifyStatus) return;
    spotifyForm = { ...spotifyForm, status: spotifyStatus === "connected" ? "Connected" : "Spotify authorization failed" };
    query.delete("spotify");
    history.replaceState({}, "", `${location.pathname}${query.size ? `?${query.toString()}` : ""}`);
  }

  function syncForms(state = editorState) {
    const currentPreferences = state?.preferences;
    if (!currentPreferences) return;
    layoutEditorProfile = layoutEditorProfile || profileKey;
    panelForm = [...(currentPreferences.visiblePanels ?? [])];
    audioTargetForm = (currentPreferences.audio?.visibleDeviceSessions ?? []).map((target) => ({
      endpointId: target.endpointId,
      sessionName: target.sessionName
    }));
    audioForm = {
      includeSystemSounds: !!currentPreferences.audio?.includeSystemSounds,
      maxSessions: Number(currentPreferences.audio?.maxSessions ?? 12),
      showDeviceLabels: currentPreferences.audio?.showDeviceLabels ?? true
    };
    discordForm = {
      enabled: !!currentPreferences.discord?.enabled,
      relayUrl: currentPreferences.discord?.relayUrl ?? "",
      apiKey: "",
      guildId: currentPreferences.discord?.guildId ?? "",
      messagesChannelId: currentPreferences.discord?.messagesChannelId ?? "",
      voiceChannelId: currentPreferences.discord?.voiceChannelId ?? "",
      trackedUserId: currentPreferences.discord?.trackedUserId ?? "",
      latestMessagesCount: Number(currentPreferences.discord?.latestMessagesCount ?? 6),
      favoriteUserIds: Array.isArray(currentPreferences.discord?.favoriteUserIds) ? currentPreferences.discord.favoriteUserIds.join("\n") : ""
    };
    spotifyForm = {
      enabled: !!currentPreferences.spotify?.enabled,
      clientId: currentPreferences.spotify?.clientId ?? "",
      status: currentPreferences.spotify?.isAuthorized ? "Connected" : (currentPreferences.spotify?.clientId ? "Ready to connect" : "Client ID required")
    };
    themeForm = {
      presetId: currentPreferences.theme?.presetId ?? "neon-grid",
      pexelsApiKey: ""
    };
    const editorLayout = getActiveLayoutProfile(currentPreferences.layout, currentLayoutEditorProfile(), viewportWidth, viewportHeight);
    layoutColumnsValue = Number(editorLayout?.columns ?? 12);
    layoutRowsValue = Number(editorLayout?.rows ?? 12);
  }

  async function saveSettings(update) {
    const response = await fetch("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(update)
    });
    if (!response.ok) throw new Error("Failed to save settings");
    editorState = await response.json();
    syncForms(editorState);
  }

  async function openSettingsDrawer() {
    if (dockInteractionsLocked()) return;
    const next = !settingsOpen;
    settingsOpen = next;
    themeOpen = false;
    if (next) {
      await refreshEditorState();
    }
  }

  const saveAudio = () => saveSettings({
    audio: {
      includeSystemSounds: audioForm.includeSystemSounds,
      maxSessions: Number(audioForm.maxSessions),
      showDeviceLabels: audioForm.showDeviceLabels,
      visibleSessionMatches: [],
      selectedEndpointId: "",
      visibleDeviceSessions: audioTargetForm.map((target) => ({ endpointId: target.endpointId, sessionName: target.sessionName }))
    }
  });

  function dockInteractionsLocked() {
    return Date.now() < dockInteractionLockUntil;
  }

  function lockDockInteractions(durationMs = 280) {
    dockInteractionLockUntil = Date.now() + durationMs;
    if (floatingDockEl) {
      floatingDockEl.classList.add("is-interaction-locked");
    }
    clearTimeout(dockInteractionTimer);
    dockInteractionTimer = setTimeout(() => {
      floatingDockEl?.classList.remove("is-interaction-locked");
    }, durationMs);
  }

  function getDockEditOffset() {
    if (!layoutEditing || !floatingDockEditEl || dockControlsHidden) return 0;
    return Math.ceil(floatingDockEditEl.getBoundingClientRect().height + 6);
  }

  function syncLayoutMetrics() {
    if (!layoutEditing) {
      if (Object.keys(panelSizeFlags).length) panelSizeFlags = {};
      return;
    }

    const next = {};
    for (const panel of Array.from(document.querySelectorAll("[data-panel]"))) {
      const key = String(panel.getAttribute("data-panel") || "").toLowerCase();
      next[key] = {
        compact: panel.clientWidth < 220 || panel.clientHeight < 120,
        tiny: panel.clientWidth < 150 || panel.clientHeight < 92
      };
    }

    if (JSON.stringify(next) !== JSON.stringify(panelSizeFlags)) {
      panelSizeFlags = next;
    }
  }

  function syncDockPlacement() {
    if (!floatingDockEl) return;

    const dock = layoutProfile?.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" };
    const viewportW = Math.max(1, window.innerWidth || viewportWidth || 1);
    const viewportH = Math.max(1, window.innerHeight || viewportHeight || 1);
    const rect = floatingDockEl.getBoundingClientRect();
    const dockWidth = Math.max(120, Math.round(rect.width || (dock.orientation === "vertical" ? 132 : 360)));
    const dockHeight = Math.max(48, Math.round(rect.height || (dock.orientation === "vertical" ? 196 : 58)));
    const offset = getDockEditOffset();
    const preferred = {
      x: Math.round(dock.x ?? 10),
      y: Math.round((dock.y ?? 10) - offset)
    };

    const clampPosition = (point) => ({
      x: Math.max(8, Math.min(Math.max(8, viewportW - dockWidth - 8), point.x)),
      y: Math.max(8, Math.min(Math.max(8, viewportH - dockHeight - 8), point.y))
    });

    const isDefaultDock = (dock.x ?? 10) === 10 && (dock.y ?? 10) === 10 && !dock.locked && (dock.orientation ?? "horizontal") === "horizontal";
    let next = clampPosition(preferred);

    if (isDefaultDock || next.x !== preferred.x || next.y !== preferred.y) {
      next = findOpenDockPosition(dockWidth, dockHeight, viewportW, viewportH) ?? next;
    }

    if (resolvedDockPosition.x !== next.x || resolvedDockPosition.y !== next.y) {
      resolvedDockPosition = next;
    }
  }

  function findOpenDockPosition(dockWidth, dockHeight, viewportW, viewportH) {
    const panelRects = Array.from(document.querySelectorAll("[data-panel]"))
      .filter((panel) => !panel.classList.contains("is-hidden"))
      .map((panel) => panel.getBoundingClientRect());

    const overlapsPanel = (candidate) => panelRects.some((rect) =>
      candidate.x < rect.right &&
      candidate.x + dockWidth > rect.left &&
      candidate.y < rect.bottom &&
      candidate.y + dockHeight > rect.top
    );

    const candidates = [
      { x: 12, y: 12 },
      { x: viewportW - dockWidth - 12, y: 12 },
      { x: 12, y: viewportH - dockHeight - 12 },
      { x: viewportW - dockWidth - 12, y: viewportH - dockHeight - 12 },
      { x: 12, y: Math.max(12, Math.round((viewportH - dockHeight) / 2)) },
      { x: viewportW - dockWidth - 12, y: Math.max(12, Math.round((viewportH - dockHeight) / 2)) }
    ];

    for (const candidate of candidates) {
      const clamped = {
        x: Math.max(8, Math.min(Math.max(8, viewportW - dockWidth - 8), candidate.x)),
        y: Math.max(8, Math.min(Math.max(8, viewportH - dockHeight - 8), candidate.y))
      };
      if (!overlapsPanel(clamped)) {
        return clamped;
      }
    }

    return null;
  }

  function toggleLayoutEditing() {
    layoutEditing = !layoutEditing;
    layoutPreview = null;
    layoutDrag = null;
    dockDrag = null;
    lockDockInteractions();
    scheduleFit();
  }

  function toggleDockControlsHidden() {
    if (!layoutEditing) return;
    dockControlsHidden = !dockControlsHidden;
  }

  function updateLayoutDockPreview(profile, patch) {
    const root = structuredClone(layoutPreview ?? preferences?.layout ?? snapshot?.ui?.layout ?? {});
    const currentProfile = getMutableLayoutTarget(root, profile);
    currentProfile.dock = {
      ...(currentProfile.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" }),
      ...patch
    };
    layoutPreview = root;
  }

  function setLayoutPreviewPanel(profile, panelKey, patch) {
    const root = structuredClone(layoutPreview ?? preferences?.layout ?? snapshot?.ui?.layout ?? {});
    const currentProfile = getMutableLayoutTarget(root, profile);
    currentProfile.panels = (currentProfile.panels ?? defaultPanelKeys.map((key) => ({ key, x: 1, y: 1, w: 1, h: 1, locked: false }))).map((panel) =>
      String(panel.key).toLowerCase() === String(panelKey).toLowerCase()
        ? { ...panel, ...patch }
        : panel);
    const resolved = resolvePriorityLayoutPanels(currentProfile.panels, currentProfile.columns, currentProfile.rows, panelKey);
    currentProfile.panels = resolved.panels;
    currentProfile.rows = Math.max(currentProfile.rows, resolved.rows);
    layoutPreview = root;
  }

  async function persistLayout(profile, update) {
    await saveSettings({
      layout: {
        profile,
        viewportKey: currentViewportKey(profile),
        viewportWidth: currentViewportDimensions().width,
        viewportHeight: currentViewportDimensions().height,
        ...update
      }
    });
  }

  async function applyLayoutFields() {
    const profile = currentLayoutEditorProfile();
    const root = structuredClone(layoutPreview ?? preferences?.layout ?? snapshot?.ui?.layout ?? {});
    const currentProfile = getMutableLayoutTarget(root, profile);
    currentProfile.columns = clampNumber(layoutColumnsValue, 1, 120);
    currentProfile.rows = clampNumber(layoutRowsValue, 1, 120);
    const resolved = normalizeLayoutPanels(currentProfile.panels ?? [], currentProfile.columns, currentProfile.rows, "");
    currentProfile.panels = resolved.panels;
    currentProfile.rows = Math.max(currentProfile.rows, resolved.rows);
    layoutPreview = root;
    await persistLayout(profile, {
      columns: currentProfile.columns,
      rows: currentProfile.rows,
      panels: currentProfile.panels.map((panel) => ({ key: panel.key, x: panel.x, y: panel.y, w: panel.w, h: panel.h, locked: panel.locked }))
    });
    layoutPreview = null;
  }

  function syncLayoutEditorFields() {
    const editorLayout = getActiveLayoutProfile(preferences?.layout ?? snapshot?.ui?.layout, currentLayoutEditorProfile(), viewportWidth, viewportHeight);
    layoutColumnsValue = Number(editorLayout?.columns ?? 12);
    layoutRowsValue = Number(editorLayout?.rows ?? 12);
  }

  async function resetLayoutProfile() {
    await saveSettings({
      layout: {
        profile: currentLayoutEditorProfile(),
        reset: true
      }
    });
    layoutPreview = null;
    syncForms(editorState);
  }

  function startDockDrag(event) {
    if (!layoutEditing || layoutProfile?.dock?.locked) return;
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    const rect = floatingDockEl?.getBoundingClientRect();
    if (!rect) return;
    dockDrag = {
      profile: profileKey,
      width: rect.width,
      height: rect.height,
      mainOffsetY: getDockEditOffset(),
      offsetX: event.clientX - rect.left,
      offsetY: event.clientY - rect.top
    };
  }

  function startDockSurfaceDrag(event) {
    if (!layoutEditing || layoutProfile?.dock?.locked) return;
    const interactive = event.target instanceof Element
      ? event.target.closest("button, a, input, select, textarea, label")
      : null;
    if (interactive) return;
    startDockDrag(event);
  }

  function startDockLinksDrag(event) {
    if (!layoutEditing || layoutProfile?.dock?.locked) return;
    startDockDrag(event);
  }

  async function toggleDockLock() {
    if (!layoutEditing) return;
    const dock = layoutProfile?.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" };
    const nextLocked = !dock.locked;
    updateLayoutDockPreview(profileKey, { locked: nextLocked });
    await persistLayout(profileKey, {
      dock: { x: dock.x, y: dock.y, locked: nextLocked, orientation: dock.orientation ?? "horizontal" }
    });
    layoutPreview = null;
  }

  async function toggleDockOrientation() {
    if (!layoutEditing) return;
    const dock = layoutProfile?.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" };
    const nextOrientation = dock.orientation === "vertical" ? "horizontal" : "vertical";
    updateLayoutDockPreview(profileKey, { orientation: nextOrientation });
    await persistLayout(profileKey, {
      dock: { x: dock.x, y: dock.y, locked: dock.locked, orientation: nextOrientation }
    });
    layoutPreview = null;
  }

  function startLayoutDrag(event, panelKey, mode) {
    if (!layoutEditing) return;
    const panel = layoutProfile?.panels?.find((item) => String(item.key).toLowerCase() === String(panelKey).toLowerCase());
    if (!panel || panel.locked) return;
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    const rect = dashboardEl?.getBoundingClientRect();
    const panelElement = document.querySelector(`[data-panel="${panelKey}"]`);
    const panelRect = panelElement?.getBoundingClientRect() ?? rect;
    if (!rect || !panelRect) return;
    layoutDrag = {
      panelKey,
      mode,
      profile: profileKey,
      initial: { ...panel },
      columns: layoutProfile.columns,
      rows: layoutProfile.rows,
      rect,
      panelRect,
      grabOffsetX: event.clientX - panelRect.left,
      grabOffsetY: event.clientY - panelRect.top
    };
  }

  function startPanelSurfaceDrag(event, panelKey) {
    if (!layoutEditing) return;
    const interactive = event.target instanceof Element
      ? event.target.closest("button, a, input, select, textarea, label")
      : null;
    if (interactive) return;
    startLayoutDrag(event, panelKey, "move");
  }

  function handlePanelPointerDown(event, panelKey) {
    if (!layoutEditing) return;
    startPanelSurfaceDrag(event, panelKey);
  }

  async function togglePanelLock(panelKey) {
    if (!layoutEditing) return;
    const panel = layoutProfile?.panels?.find((item) => String(item.key).toLowerCase() === String(panelKey).toLowerCase());
    if (!panel) return;
    const nextLocked = !panel.locked;
    setLayoutPreviewPanel(profileKey, panel.key, { locked: nextLocked });
    await persistLayout(profileKey, {
      panels: [{ key: panel.key, locked: nextLocked }]
    });
    layoutPreview = null;
  }

  function onGlobalPointerMove(event) {
    if (dockDrag) {
      event.preventDefault();
      const maxX = Math.max(0, window.innerWidth - dockDrag.width - 8);
      const maxY = Math.max(0, window.innerHeight - dockDrag.height - 8);
      const x = Math.max(8, Math.min(maxX, Math.round(event.clientX - dockDrag.offsetX)));
      const top = Math.max(8, Math.min(maxY, Math.round(event.clientY - dockDrag.offsetY)));
      const y = Math.max(8, top + (dockDrag.mainOffsetY ?? 0));
      updateLayoutDockPreview(dockDrag.profile, { x, y });
      return;
    }

    if (!layoutDrag) return;
    event.preventDefault();
    const drag = layoutDrag;
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
  }

  async function finalizeLayoutDrag() {
    const drag = layoutDrag;
    layoutDrag = null;
    if (!drag || !layoutPreview) return;
    const layout = getActiveLayoutProfile(layoutPreview, drag.profile, viewportWidth, viewportHeight);
    await persistLayout(drag.profile, {
      columns: layout.columns,
      rows: layout.rows,
      panels: (layout.panels ?? []).map((item) => ({ key: item.key, x: item.x, y: item.y, w: item.w, h: item.h, locked: item.locked }))
    });
    layoutPreview = null;
  }

  async function finalizeDockDrag() {
    const drag = dockDrag;
    dockDrag = null;
    if (!drag || !layoutPreview) return;
    const dock = getActiveLayoutProfile(layoutPreview, drag.profile, viewportWidth, viewportHeight)?.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" };
    await persistLayout(drag.profile, {
      dock: { x: dock.x, y: dock.y, locked: dock.locked, orientation: dock.orientation ?? "horizontal" }
    });
    layoutPreview = null;
  }

  function onGlobalPointerRelease(event) {
    if (dockDrag) {
      finalizeDockDrag();
      return;
    }
    if (layoutDrag) {
      finalizeLayoutDrag();
      return;
    }

    const sessionId = audioDraggingSessionId;
    if (!sessionId) return;
    const slider = event.target instanceof Element
      ? event.target.closest(".audio-row")?.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`) ?? document.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`)
      : document.querySelector(`.slider[data-session-id="${CSS.escape(sessionId)}"]`);
    if (slider) {
      finalizeSliderInteraction(slider);
      return;
    }
    audioDraggingSessionId = null;
  }

  async function saveDiscord() {
    const update = {
      enabled: discordForm.enabled,
      relayUrl: discordForm.relayUrl.trim(),
      guildId: discordForm.guildId.trim(),
      messagesChannelId: discordForm.messagesChannelId.trim(),
      voiceChannelId: discordForm.voiceChannelId.trim(),
      trackedUserId: discordForm.trackedUserId.trim(),
      latestMessagesCount: Number(discordForm.latestMessagesCount || 6),
      favoriteUserIds: discordForm.favoriteUserIds.split(/\r?\n|,/).map((item) => item.trim()).filter(Boolean)
    };
    if (discordForm.apiKey.trim()) update.apiKey = discordForm.apiKey.trim();
    await saveSettings({ discord: update });
    discordForm = { ...discordForm, apiKey: "" };
  }

  async function saveSpotify() {
    await saveSettings({ spotify: { enabled: spotifyForm.enabled, clientId: spotifyForm.clientId.trim() } });
    spotifyForm = { ...spotifyForm, status: spotifyForm.clientId.trim() ? "Ready to connect" : "Client ID required" };
  }

  async function connectSpotify() {
    if (!spotifyForm.clientId.trim()) {
      spotifyForm = { ...spotifyForm, status: "Client ID required" };
      return;
    }
    await saveSpotify();
    location.href = `/api/spotify/connect/start?returnUrl=${encodeURIComponent("/studio/index.html")}`;
  }

  async function disconnectSpotify() {
    await fetch("/api/spotify/disconnect", { method: "POST" });
    const response = await fetch("/api/settings", { cache: "no-store" });
    if (response.ok) {
      editorState = await response.json();
      syncForms();
      spotifyForm = { ...spotifyForm, status: "Disconnected" };
    }
  }

  async function savePexelsKey() {
    await saveSettings({ theme: { pexelsApiKey: themeForm.pexelsApiKey } });
    themeForm = { ...themeForm, pexelsApiKey: "" };
  }

  const setPreset = (presetId) => saveSettings({ theme: { presetId } });

  async function uploadThemeMedia(event) {
    const file = event.currentTarget.files?.[0];
    if (!file) return;
    const body = new FormData();
    body.append("file", file);
    const response = await fetch("/api/media/upload", { method: "POST", body });
    if (response.ok) {
      const asset = await response.json();
      localMedia = [asset, ...localMedia.filter((item) => item.id !== asset.id)];
    }
    event.currentTarget.value = "";
  }

  async function saveThemeBackground(backgroundUpdate, urls = []) {
    await saveSettings({ theme: { background: backgroundUpdate } });
    queueThemeCache(urls.filter(Boolean));
  }

  const selectLocalMedia = (asset) => saveThemeBackground({ source: "local", mediaKind: asset.mediaKind, assetId: asset.id, label: asset.name, renderUrl: asset.url, previewUrl: asset.previewUrl, attribution: "", attributionUrl: "" }, [asset.url, asset.previewUrl]);

  async function deleteLocalMedia(assetId) {
    const response = await fetch(`/api/media/local/${encodeURIComponent(assetId)}`, { method: "DELETE" });
    if (response.ok) localMedia = localMedia.filter((asset) => asset.id !== assetId);
  }
  async function runPexelsSearch(page = 1) {
    if (!pexelsQuery.trim()) {
      pexelsWarning = "Enter a search query first.";
      pexelsResult = null;
      return;
    }
    const query = new URLSearchParams({ query: pexelsQuery.trim(), mediaKind: pexelsKind, page: String(page), perPage: "18" });
    const response = await fetch(`/api/media/pexels/search?${query.toString()}`, { cache: "no-store" });
    if (!response.ok) {
      const error = await response.json().catch(() => null);
      pexelsWarning = error?.error || "Pexels search failed.";
      pexelsResult = null;
      return;
    }
    pexelsWarning = "";
    pexelsResult = await response.json();
  }

  const selectPexelsResult = (asset) => saveThemeBackground({ source: asset.mediaKind === "video" ? "pexels-video" : "pexels-photo", mediaKind: asset.mediaKind, assetId: asset.id, label: asset.label, renderUrl: asset.renderUrl, previewUrl: asset.previewUrl, attribution: asset.attribution, attributionUrl: asset.attributionUrl }, [asset.renderUrl, asset.previewUrl]);

  const wsSend = (payload) => { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(payload)); };

  async function sendSpotifyCommand(command) {
    const response = await fetch("/api/spotify/command", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(command)
    });
    if (!response.ok) {
      const error = await response.json().catch(() => null);
      if (snapshot?.spotify) snapshot = { ...snapshot, spotify: { ...snapshot.spotify, warning: error?.error || "Spotify command failed." } };
      return;
    }
    snapshot = { ...snapshot, spotify: await response.json() };
    spotifySeekPreview = null;
    spotifyVolumePreview = null;
    syncSpotifyTimer();
  }

  function syncSpotifyTimer() {
    clearInterval(spotifyProgressTimer);
    if (!snapshot?.spotify?.nowPlaying?.isPlaying) return;
    spotifyProgressTimer = setInterval(() => {
      if (!snapshot?.spotify?.nowPlaying?.isPlaying) return;
      snapshot = { ...snapshot, spotify: { ...snapshot.spotify, nowPlaying: { ...snapshot.spotify.nowPlaying, progressMs: Math.min(snapshot.spotify.nowPlaying.durationMs, snapshot.spotify.nowPlaying.progressMs + 500) } } };
    }, 500);
  }

  function queueThemeCache(urls) {
    if (!mediaCacheReady || !navigator.serviceWorker?.controller || !urls.length) return;
    navigator.serviceWorker.controller.postMessage({ type: "cache-assets", urls });
  }

  async function toggleFullscreen() {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await document.documentElement.requestFullscreen();
    } catch {
    }
  }

  function updateViewport() {
    viewportWidth = Math.max(1, Math.round(window.innerWidth));
    viewportHeight = Math.max(1, Math.round(window.innerHeight));
    scheduleFit();
  }

  function scheduleFit() {
    cancelAnimationFrame(fitFrame);
    fitFrame = requestAnimationFrame(fitDashboardToViewport);
  }

  function fitDashboardToViewport() {
    if (!viewportEl || !dashboardEl) return;
    if (layoutMode !== "phone-landscape") {
      fitScale = 1;
      fitWidth = "";
      fitHeight = "";
      fitMarginLeft = "";
      return;
    }
    const availableWidth = viewportEl.clientWidth;
    const availableHeight = viewportEl.clientHeight;
    const naturalWidth = dashboardEl.scrollWidth;
    const naturalHeight = dashboardEl.scrollHeight;
    if (!availableWidth || !availableHeight || !naturalWidth || !naturalHeight) return;
    const widthScale = availableWidth / naturalWidth;
    const heightScale = availableHeight / naturalHeight;
    fitScale = Math.min(1, naturalHeight * widthScale <= availableHeight ? widthScale : heightScale);
    fitWidth = `${naturalWidth}px`;
    fitHeight = `${naturalHeight}px`;
    fitMarginLeft = `${Math.max(0, (availableWidth - naturalWidth * fitScale) / 2)}px`;
  }

  function currentProfileKey(width, height) {
    const landscape = width > height;
    if (landscape && width <= 980) return "phone-landscape";
    if (landscape) return "tablet-landscape";
    return "desktop";
  }

  function currentViewportDimensions() {
    return {
      width: Math.max(1, Math.round(viewportWidth || window.innerWidth || 1)),
      height: Math.max(1, Math.round(viewportHeight || window.innerHeight || 1))
    };
  }

  function currentViewportKey(profile = profileKey) {
    const { width, height } = currentViewportDimensions();
    return `${profile}@${width}x${height}`;
  }

  function currentLayoutEditorProfile() {
    if (!layoutEditorProfile) {
      layoutEditorProfile = profileKey;
    }
    return layoutEditorProfile;
  }

  function layoutProfileProperty(profile) {
    return profile === "phone-landscape"
      ? "phoneLandscape"
      : profile === "tablet-landscape"
        ? "tabletLandscape"
        : "desktop";
  }

  function fallbackLayoutProfile() {
    return {
      columns: 12,
      rows: 12,
      panels: defaultPanelKeys.map((key, index) => ({ key, x: (index % 3) * 4 + 1, y: Math.floor(index / 3) * 4 + 1, w: 4, h: 4, locked: false })),
      dock: { x: 10, y: 10, locked: false, orientation: "horizontal" },
      variants: []
    };
  }

  function getLayoutProfile(layout, profile) {
    const source = profile === "phone-landscape" ? layout?.phoneLandscape : profile === "tablet-landscape" ? layout?.tabletLandscape : layout?.desktop;
    return source ?? fallbackLayoutProfile();
  }

  function findBestLayoutVariant(base, viewportKey, width, height) {
    const variants = Array.isArray(base?.variants) ? base.variants : [];
    if (!variants.length) return null;
    const exact = variants.find((item) => item.viewportKey === viewportKey);
    if (exact) return exact;
    return [...variants].sort((a, b) => (Math.abs((a.viewportWidth ?? width) - width) + Math.abs((a.viewportHeight ?? height) - height)) - (Math.abs((b.viewportWidth ?? width) - width) + Math.abs((b.viewportHeight ?? height) - height)))[0] ?? null;
  }

  function getActiveLayoutProfile(layout, profile, width, height) {
    const base = getLayoutProfile(layout, profile);
    const variant = findBestLayoutVariant(base, `${profile}@${width}x${height}`, width, height);
    if (!variant) {
      return {
        ...base,
        dock: base.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" }
      };
    }

    return {
      ...base,
      columns: variant.columns,
      rows: variant.rows,
      panels: variant.panels,
      dock: variant.dock ?? base.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" }
    };
  }

  function getMutableLayoutTarget(root, profile) {
    const property = layoutProfileProperty(profile);
    const baseProfile = structuredClone(root?.[property] ?? getLayoutProfile(root, profile));
    root[property] = baseProfile;
    const viewportKey = currentViewportKey(profile);
    const { width, height } = currentViewportDimensions();
    baseProfile.variants = Array.isArray(baseProfile.variants) ? [...baseProfile.variants] : [];
    let index = baseProfile.variants.findIndex((item) => item.viewportKey === viewportKey);
    if (index < 0) {
      baseProfile.variants.push({
        viewportKey,
        viewportWidth: width,
        viewportHeight: height,
        columns: baseProfile.columns,
        rows: baseProfile.rows,
        panels: structuredClone(baseProfile.panels ?? []),
        dock: structuredClone(baseProfile.dock ?? { x: 10, y: 10, locked: false, orientation: "horizontal" })
      });
      index = baseProfile.variants.length - 1;
    }
    const variant = structuredClone(baseProfile.variants[index]);
    baseProfile.variants[index] = variant;
    return variant;
  }

  function panelsOverlap(left, right) {
    return left.x < right.x + right.w
      && left.x + left.w > right.x
      && left.y < right.y + right.h
      && left.y + left.h > right.y;
  }

  function findOpenLayoutSlot(panel, placed, columns, rows) {
    const maxY = Math.max(1, Math.max(rows, 240) - panel.h + 1);
    const maxX = Math.max(1, columns - panel.w + 1);
    const preferredY = clampNumber(panel.y, 1, maxY);
    const preferredX = clampNumber(panel.x, 1, maxX);
    for (let distance = 0; distance <= rows + columns; distance += 1) {
      for (let y = 1; y <= maxY; y += 1) {
        for (let x = 1; x <= maxX; x += 1) {
          if (Math.abs(y - preferredY) + Math.abs(x - preferredX) !== distance) continue;
          const candidate = { ...panel, x, y };
          if (!placed.some((other) => panelsOverlap(other, candidate))) {
            return candidate;
          }
        }
      }
    }
    return { ...panel, x: preferredX, y: maxY };
  }

  function normalizeLayoutPanels(panels, columns, rows, priorityKey = "") {
    const source = (Array.isArray(panels) ? panels : []).map((panel) => ({ ...panel, locked: !!panel.locked }));
    const lockedPanels = source.filter((panel) => panel.locked).sort((a, b) => (a.y - b.y) || (a.x - b.x));
    const movablePanels = source.filter((panel) => !panel.locked).sort((a, b) => {
      const leftPriority = String(a.key).toLowerCase() === String(priorityKey).toLowerCase() ? -1 : 0;
      const rightPriority = String(b.key).toLowerCase() === String(priorityKey).toLowerCase() ? -1 : 0;
      if (leftPriority !== rightPriority) return leftPriority - rightPriority;
      if (a.y !== b.y) return a.y - b.y;
      return a.x - b.x;
    });

    const placed = [];
    let requiredRows = rows;
    for (const panel of lockedPanels) {
      const normalized = { ...panel, x: clampNumber(panel.x, 1, columns), y: clampNumber(panel.y, 1, rows), w: clampNumber(panel.w, 1, columns), h: clampNumber(panel.h, 1, rows) };
      normalized.w = clampNumber(normalized.w, 1, columns - normalized.x + 1);
      normalized.h = clampNumber(normalized.h, 1, rows - normalized.y + 1);
      requiredRows = Math.max(requiredRows, normalized.y + normalized.h - 1);
      placed.push(normalized);
    }

    for (const panel of movablePanels) {
      const normalized = { ...panel, x: clampNumber(panel.x, 1, columns), y: clampNumber(panel.y, 1, rows), w: clampNumber(panel.w, 1, columns), h: clampNumber(panel.h, 1, rows) };
      normalized.w = clampNumber(normalized.w, 1, columns - normalized.x + 1);
      normalized.h = clampNumber(normalized.h, 1, rows - normalized.y + 1);
      if (!placed.some((other) => panelsOverlap(other, normalized))) {
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
    const byKey = new Map(normalized.map((panel) => [String(panel.key).toLowerCase(), { ...panel }]));
    const priority = byKey.get(String(priorityKey).toLowerCase());
    if (!priority) return { panels: normalized, rows: requiredRows };
    const lockedAnchors = normalized.filter((panel) => String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase() && panel.locked);
    const anchoredPriority = lockedAnchors.some((panel) => panelsOverlap(panel, priority)) ? findOpenLayoutSlot(priority, lockedAnchors, columns, requiredRows) : priority;
    requiredRows = Math.max(requiredRows, anchoredPriority.y + anchoredPriority.h - 1);
    const fixedMovable = normalized.filter((panel) => String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase() && !panel.locked && !panelsOverlap(panel, anchoredPriority));
    const displaced = normalized.filter((panel) => String(panel.key).toLowerCase() !== String(priorityKey).toLowerCase() && !panel.locked && panelsOverlap(panel, anchoredPriority));
    const occupied = [...lockedAnchors, ...fixedMovable, anchoredPriority];
    const relocated = [];
    for (const panel of displaced) {
      const next = findOpenLayoutSlot(panel, [...occupied, ...relocated], columns, requiredRows);
      requiredRows = Math.max(requiredRows, next.y + next.h - 1);
      relocated.push(next);
    }
    const resolved = new Map();
    resolved.set(String(priorityKey).toLowerCase(), anchoredPriority);
    [...lockedAnchors, ...fixedMovable, ...relocated].forEach((panel) => resolved.set(String(panel.key).toLowerCase(), panel));
    return {
      panels: normalized.map((panel) => resolved.get(String(panel.key).toLowerCase()) ?? panel),
      rows: requiredRows
    };
  }

  function clampNumber(value, min, max) {
    const numeric = Number(value);
    if (Number.isNaN(numeric)) return min;
    return Math.min(max, Math.max(min, numeric));
  }

  const formatNumber = (value) => Number(value || 0).toFixed(0);
  const pointsFor = (values) => {
    const items = Array.isArray(values) && values.length ? values : [0];
    const max = Math.max(...items, 1);
    return items.map((value, index) => {
      const x = items.length === 1 ? 0 : (index / (items.length - 1)) * 300;
      const y = 68 - ((value / max) * 62);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(" ");
  };

  const formatDuration = (value) => {
    const totalSeconds = Math.max(0, Math.floor((Number(value) || 0) / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  };

  function compactSystemValue(kind, value) {
    let text = String(value ?? "").trim();
    if (!text) return "";
    if (kind === "cpu") text = text.replace(/^AMD\s+/i, "").replace(/^Intel\(R\)\s+/i, "").replace(/\s+\d+-Core Processor$/i, "").replace(/\s+Processor$/i, "").replace(/\s+CPU$/i, "");
    if (kind === "gpu") text = text.replace(/^NVIDIA\s+/i, "").replace(/^AMD\s+/i, "").replace(/^Intel\(R\)\s+/i, "").replace(/^GeForce\s+/i, "").replace(/^Radeon\s+/i, "").replace(/^Graphics\s+/i, "");
    if (kind === "board") text = text.replace(/\s+\([^)]*\)\s*$/i, "").replace(/^Micro-Star International Co\., Ltd\.\s*/i, "").replace(/^ASUSTeK COMPUTER INC\.\s*/i, "").replace(/^Gigabyte Technology Co\., Ltd\.\s*/i, "");
    if (kind === "os") text = text.replace(/^Microsoft\s+/i, "");
    return text.replace(/\s{2,}/g, " ").trim();
  }

  const nextRepeatState = (currentState) => currentState === "off" ? "context" : currentState === "context" ? "track" : "off";
  const compactEndpointName = (value, isDefault) => {
    const original = String(value ?? "").trim();
    if (!original) return isDefault ? "Default" : "Audio Device";
    return original.replace(/[()]+/g, " ").replace(/\s{2,}/g, " ").trim() || (isDefault ? "Default" : "Audio Device");
  };
  const settingsMask = (value) => String(value ?? "").trim() || "No key saved yet.";
  const isDiscordSession = (session) => String(session?.name ?? "").trim().toLowerCase() === "discord";

  function patchAudioInventory(mutator) {
    if (!editorState?.audioInventory) return;
    const nextInventory = structuredClone(editorState.audioInventory);
    mutator(nextInventory);
    editorState = { ...editorState, audioInventory: nextInventory };
  }

  function patchOutputMaster(endpointId, patch) {
    patchAudioInventory((inventory) => {
      inventory.outputDevices = (inventory.outputDevices ?? []).map((device) =>
        device.id === endpointId
          ? { ...device, master: { ...device.master, ...patch } }
          : device);
    });
  }

  function patchAllInputMute(nextMuted) {
    patchAudioInventory((inventory) => {
      inventory.inputDevices = (inventory.inputDevices ?? []).map((device) => ({ ...device, isMuted: nextMuted }));
    });
  }

  function patchDiscordOutputsMute(nextMuted) {
    patchAudioInventory((inventory) => {
      inventory.outputDevices = (inventory.outputDevices ?? []).map((device) => ({
        ...device,
        sessions: (device.sessions ?? []).map((session) =>
          isDiscordSession(session)
            ? { ...session, isMuted: nextMuted }
            : session)
      }));
    });
  }

  function refreshEditorStateSoon(delayMs = 320) {
    setTimeout(() => {
      if (settingsOpen) {
        refreshEditorState();
      }
    }, delayMs);
  }

  async function togglePanel(panelKey) {
    const current = new Set(panelForm.map((item) => String(item).toLowerCase()));
    const normalizedKey = String(panelKey).toLowerCase();
    if (current.has(normalizedKey) && current.size === 1) return;
    current.has(normalizedKey) ? current.delete(normalizedKey) : current.add(normalizedKey);
    const nextPanels = Array.from(current);
    panelForm = nextPanels;
    editorState = editorState
      ? {
          ...editorState,
          preferences: {
            ...editorState.preferences,
            visiblePanels: nextPanels
          }
        }
      : editorState;
    try {
      await saveSettings({ visiblePanels: nextPanels });
    } catch {
      await loadInitial();
    }
  }

  function hasVisibleAudioTarget(endpointId, sessionName) {
    return audioTargetForm.some((target) =>
      target.endpointId === endpointId && target.sessionName.toLowerCase() === String(sessionName).toLowerCase());
  }

  function visibleTargetsForDevice(endpointId) {
    return audioTargetForm.filter((target) => target.endpointId === endpointId).length;
  }

  async function toggleAudioTarget(endpointId, sessionName) {
    const normalizedName = String(sessionName).trim();
    const exists = hasVisibleAudioTarget(endpointId, normalizedName);
    const nextTargets = exists
      ? audioTargetForm.filter((target) => !(target.endpointId === endpointId && target.sessionName.toLowerCase() === normalizedName.toLowerCase()))
      : [...audioTargetForm, { endpointId, sessionName: normalizedName }];
    audioTargetForm = nextTargets;
    editorState = editorState
      ? {
          ...editorState,
          preferences: {
            ...editorState.preferences,
            audio: {
              ...editorState.preferences.audio,
              showDeviceLabels: audioForm.showDeviceLabels,
              visibleDeviceSessions: nextTargets.map((target) => ({ endpointId: target.endpointId, sessionName: target.sessionName }))
            }
          }
        }
      : editorState;
    try {
      await saveSettings({
        audio: {
          includeSystemSounds: audioForm.includeSystemSounds,
          maxSessions: Number(audioForm.maxSessions),
          showDeviceLabels: audioForm.showDeviceLabels,
          visibleSessionMatches: [],
          selectedEndpointId: "",
          visibleDeviceSessions: nextTargets.map((target) => ({ endpointId: target.endpointId, sessionName: target.sessionName }))
        }
      });
    } catch {
      await loadInitial();
    }
  }

  const themePages = () => {
    const current = pexelsResult?.page ?? 1;
    const pages = [current];
    if (current > 1) pages.unshift(current - 1);
    if (pexelsResult?.nextPage) pages.push(current + 1);
    return pages;
  };
  const selectedPexels = (asset) => background?.source?.startsWith("pexels") && background?.assetId === asset.id;
  function pruneAudioOptimisticState() {
    const now = Date.now();
    const next = {};
    Object.entries(audioOptimistic).forEach(([key, value]) => {
      if ((value.expiresAt ?? 0) > now) next[key] = value;
    });
    audioOptimistic = next;
  }

  function updateOptimisticAudioState(sessionId, patch, holdMs = 1800) {
    audioOptimistic = { ...audioOptimistic, [sessionId]: { ...(audioOptimistic[sessionId] ?? {}), ...patch, expiresAt: Date.now() + holdMs } };
  }

  function applyOptimisticAudioState(session) {
    const optimistic = audioOptimistic[session.id];
    if (!optimistic || (optimistic.expiresAt ?? 0) <= Date.now()) return session;
    return { ...session, volumePercent: optimistic.volumePercent ?? session.volumePercent, isMuted: optimistic.isMuted ?? session.isMuted };
  }

  function throttleCommand(key, callback, intervalMs) {
    const now = Date.now();
    const lastSentAt = commandLastSentAt.get(key) ?? 0;
    const elapsed = now - lastSentAt;
    if (elapsed >= intervalMs) {
      clearTimeout(commandTimers.get(key));
      commandTimers.delete(key);
      commandLastSentAt.set(key, now);
      callback();
      return;
    }
    clearTimeout(commandTimers.get(key));
    commandTimers.set(key, setTimeout(() => {
      commandLastSentAt.set(key, Date.now());
      callback();
      commandTimers.delete(key);
    }, intervalMs - elapsed));
  }

  function onAudioInput(sessionId, event) {
    audioDraggingSessionId = sessionId;
    const volumePercent = Number(event.currentTarget.value);
    updateOptimisticAudioState(sessionId, { volumePercent }, 2500);
    throttleCommand(`audio:${sessionId}:volume`, () => wsSend({ type: "setVolume", sessionId, value: volumePercent / 100 }), 75);
  }

  function onAudioChange(sessionId, event) {
    const volumePercent = Number(event.currentTarget.value);
    updateOptimisticAudioState(sessionId, { volumePercent }, 2500);
    wsSend({ type: "setVolume", sessionId, value: volumePercent / 100 });
  }

  function onAudioMute(session) {
    const nextMuted = !session.isMuted;
    updateOptimisticAudioState(session.id, { isMuted: nextMuted }, 2500);
    wsSend({ type: "setMute", sessionId: session.id, value: nextMuted ? 1 : 0 });
  }

  function onInventoryMasterInput(endpointId, event) {
    const sessionId = `master-output|${endpointId}`;
    const volumePercent = Number(event.currentTarget.value);
    patchOutputMaster(endpointId, { volumePercent });
    throttleCommand(`audio:${sessionId}:volume`, () => wsSend({ type: "setVolume", sessionId, value: volumePercent / 100 }), 75);
  }

  function onInventoryMasterChange(endpointId, event) {
    const sessionId = `master-output|${endpointId}`;
    const volumePercent = Number(event.currentTarget.value);
    patchOutputMaster(endpointId, { volumePercent });
    wsSend({ type: "setVolume", sessionId, value: volumePercent / 100 });
    refreshEditorStateSoon();
  }

  function onInventoryMasterMute(device) {
    const sessionId = `master-output|${device.id}`;
    const nextMuted = !device.master.isMuted;
    patchOutputMaster(device.id, { isMuted: nextMuted });
    wsSend({ type: "setMute", sessionId, value: nextMuted ? 1 : 0 });
    refreshEditorStateSoon();
  }

  function toggleAllInputsMute() {
    const nextMuted = !allInputsMuted;
    patchAllInputMute(nextMuted);
    wsSend({ type: "setAllInputMute", value: nextMuted ? 1 : 0 });
    refreshEditorStateSoon();
  }

  function toggleDiscordOutputsMute() {
    const nextMuted = !discordOutputsMuted;
    patchDiscordOutputsMute(nextMuted);
    wsSend({ type: "setDiscordOutputsMute", value: nextMuted ? 1 : 0 });
    refreshEditorStateSoon();
  }

  function toggleDiscordPrivacyMode() {
    const nextMuted = !(allInputsMuted && discordOutputsMuted);
    patchAllInputMute(nextMuted);
    patchDiscordOutputsMute(nextMuted);
    wsSend({ type: "setDiscordPrivacyMode", value: nextMuted ? 1 : 0 });
    refreshEditorStateSoon();
  }

  function finalizeSliderInteraction(slider) {
    if (!slider) return;
    const sessionId = slider.dataset.sessionId;
    if (!sessionId) return;
    const volumePercent = Number(slider.value);
    updateOptimisticAudioState(sessionId, { volumePercent }, 2500);
    wsSend({ type: "setVolume", sessionId, value: volumePercent / 100 });
    audioDraggingSessionId = null;
  }

  async function onSpotify(action, extras = {}) {
    if (action === "copy-link") {
      const url = extras.url || spotifyNow?.trackUrl;
      if (url && navigator.clipboard?.writeText) await navigator.clipboard.writeText(url).catch(() => {});
      return;
    }
    await sendSpotifyCommand({
      action,
      itemId: extras.itemId ?? null,
      deviceId: extras.deviceId ?? spotifyNow?.deviceId ?? null,
      repeatState: extras.repeatState ?? null,
      value: extras.value ?? null
    });
  }

  function onSeekInput(event) {
    spotifySeekPreview = Number(event.currentTarget.value);
    clearTimeout(spotifySeekTimer);
    spotifySeekTimer = setTimeout(() => onSpotify("seek", { value: spotifySeekPreview }), 120);
  }

  function onSeekChange(event) {
    spotifySeekPreview = Number(event.currentTarget.value);
    clearTimeout(spotifySeekTimer);
    onSpotify("seek", { value: spotifySeekPreview });
  }

  function onVolumeInput(event) {
    spotifyVolumePreview = Number(event.currentTarget.value);
    clearTimeout(spotifyVolumeTimer);
    spotifyVolumeTimer = setTimeout(() => onSpotify("volume", { value: spotifyVolumePreview }), 90);
  }

  function onVolumeChange(event) {
    spotifyVolumePreview = Number(event.currentTarget.value);
    clearTimeout(spotifyVolumeTimer);
    onSpotify("volume", { value: spotifyVolumePreview });
  }

  const systemRows = (system) => [
    ["Host", system?.hostName],
    ["CPU", compactSystemValue("cpu", system?.cpu)],
    ["GPU", compactSystemValue("gpu", system?.gpu)],
    ["RAM", system?.ram],
    ["Board", compactSystemValue("board", system?.board)],
    ["OS", compactSystemValue("os", system?.os)],
    ["Monitor", system?.monitor],
    ["Uptime", system?.uptime]
  ];
</script>

<div class="theme-media-layer">
  {#if background?.renderUrl}
    {#if background.mediaKind === "video"}
      <video class="theme-background-video" src={background.renderUrl} poster={background.previewUrl || ""} autoplay muted loop playsinline preload="auto"></video>
    {:else}
      <img class="theme-background-image" src={background.renderUrl} alt={background.label || "Studio background"}>
    {/if}
  {/if}
</div>
<div class="theme-media-scrim"></div>

<div class={`floating-dock ${(layoutProfile?.dock?.orientation === "vertical") ? "is-vertical" : ""} ${layoutEditing && !(layoutProfile?.dock?.locked) ? "is-draggable" : ""}`.trim()} role="group" aria-label="Studio controls" bind:this={floatingDockEl} style={dockCssText} on:pointerdown={startDockSurfaceDrag}>
  <div class={`floating-dock-edit ${dockControlsHidden ? "is-collapsed" : ""}`.trim()} role="presentation" bind:this={floatingDockEditEl} on:pointerdown={startDockSurfaceDrag}>
    <button class="dock-move-handle" type="button" aria-label="Move controls" on:pointerdown={startDockDrag}>Move</button>
    <button class={`dock-orientation-toggle ${(layoutProfile?.dock?.orientation === "vertical") ? "is-active" : ""}`.trim()} type="button" aria-label="Switch controls orientation" on:click={toggleDockOrientation}>{layoutProfile?.dock?.orientation === "vertical" ? "Vertical" : "Horizontal"}</button>
    <button class={`dock-lock-toggle ${(layoutProfile?.dock?.locked) ? "is-locked" : ""}`.trim()} type="button" aria-label={(layoutProfile?.dock?.locked) ? "Unlock controls" : "Lock controls"} on:click={toggleDockLock}>{(layoutProfile?.dock?.locked) ? "Locked" : "Lock"}</button>
    <button class="dock-visibility-toggle" hidden={dockControlsHidden} type="button" aria-label="Hide layout controls" on:click={toggleDockControlsHidden}>Hide</button>
  </div>
  <div class="floating-dock-main" role="presentation" on:pointerdown={startDockSurfaceDrag}>
    <div class={`client-mode-links ${layoutEditing && !(layoutProfile?.dock?.locked) ? "is-drag-handle" : ""}`.trim()} role="presentation" on:pointerdown={startDockLinksDrag}>
      <a class="client-mode-link is-active" href="/studio/" on:pointerdown={startDockLinksDrag}>Studio</a>
      <a class="client-mode-link" href="/vanilla/" on:pointerdown={startDockLinksDrag}>Vanilla</a>
      <button class="dock-reveal-toggle" hidden={!layoutEditing || !dockControlsHidden} type="button" aria-label="Show layout controls" on:click={toggleDockControlsHidden}>↑</button>
    </div>
      <button class={`theme-toggle ${themeOpen ? "is-active" : ""}`.trim()} type="button" aria-label="Open theme studio" on:click={() => { if (dockInteractionsLocked()) return; themeOpen = !themeOpen; settingsOpen = false; }}>✦</button>
    <button class="fullscreen-toggle" type="button" aria-label="Toggle fullscreen" on:click={() => { if (dockInteractionsLocked()) return; toggleFullscreen(); }}>⤢</button>
    <button class="settings-toggle" type="button" aria-label="Open live controls" on:click={openSettingsDrawer}>⚙</button>
    <button class={`layout-toggle ${layoutEditing ? "is-active" : ""}`.trim()} type="button" aria-label={layoutEditing ? "Lock layout editing" : "Unlock layout editing"} on:click={toggleLayoutEditing}>{layoutEditing ? "Lock" : "Grid"}</button>
  </div>
</div>

<button class="settings-backdrop" hidden={!settingsOpen} type="button" aria-label="Close settings" on:click={() => (settingsOpen = false)}></button>
<button class="theme-backdrop" hidden={!themeOpen} type="button" aria-label="Close theme drawer" on:click={() => (themeOpen = false)}></button>

<aside class="settings-drawer" aria-hidden={!settingsOpen}>
  <form class="drawer-form" autocomplete="off" on:submit|preventDefault>
  <input class="visually-hidden" type="text" name="settings-secret-context" autocomplete="username" tabindex="-1" aria-hidden="true" value="studio-settings">
  <div class="settings-header">
    <div>
      <div class="eyebrow">Live Controls</div>
      <div class="settings-subtitle">Panels, audio scope, Discord relay, and Spotify integration.</div>
    </div>
    <button class="settings-close" type="button" on:click={() => (settingsOpen = false)}>✕</button>
  </div>

  <div class="settings-workspace">
    <nav class="settings-nav" aria-label="Settings sections">
      {#each settingsTabs as tab}
        <button class={`settings-nav-pill ${settingsSection === tab.key ? "is-active" : ""}`.trim()} type="button" on:click={() => (settingsSection = tab.key)}>{tab.label}</button>
      {/each}
    </nav>

    <div class="settings-content">
      {#if settingsSection === "panels"}
        <section class="settings-section">
          <div class="section-title">Panels</div>
          <div class="settings-subtitle">Show only the panels you want on this device layout.</div>
          <div class="toggle-grid">
            {#each editorState?.availablePanels ?? [] as panel}
              <button class={`toggle-pill ${panelForm.map((value) => value.toLowerCase()).includes(panel.key) ? "is-active" : ""}`.trim()} type="button" on:click={() => togglePanel(panel.key)}>{panel.label}</button>
            {/each}
          </div>
        </section>
      {/if}

      {#if settingsSection === "audio"}
        <div class="settings-panel-grid settings-panel-grid-audio">
          <section class="settings-section">
            <div class="settings-row">
              <div class="section-title">Mixer Scope</div>
              <button class="ghost-button" type="button" on:click={saveAudio}>Apply</button>
            </div>
            <div class="settings-subtitle">Pick how many rows the mixer can use, then tick the app and device targets you want exposed in the panel.</div>
            <div class="settings-range-row">
              <label class="settings-label" for="settings-max-sessions">Visible mixer rows</label>
              <div class="settings-range-value">{audioForm.maxSessions}</div>
            </div>
            <input id="settings-max-sessions" name="settings-max-sessions" class="settings-range" type="range" min="1" max="16" step="1" bind:value={audioForm.maxSessions}>
            <label class="settings-switch">
              <input id="settings-include-system-sounds" name="settings-include-system-sounds" type="checkbox" bind:checked={audioForm.includeSystemSounds}>
              <span>Include system sounds</span>
            </label>
            <label class="settings-switch">
              <input id="settings-show-device-labels" name="settings-show-device-labels" type="checkbox" bind:checked={audioForm.showDeviceLabels}>
              <span>Show output device labels in mixer</span>
            </label>
            <div class="settings-inline-actions">
              <button class={`ghost-button ${allInputsMuted ? "is-active" : ""}`.trim()} type="button" on:click={toggleAllInputsMute}>{allInputsMuted ? "Unmute all mics" : "Mute all mics"}</button>
              <button class={`ghost-button ${discordOutputsMuted ? "is-active" : ""}`.trim()} type="button" disabled={!discordOutputSessions.length} on:click={toggleDiscordOutputsMute}>{discordOutputsMuted ? "Unmute Discord outputs" : "Mute Discord outputs"}</button>
              <button class={`ghost-button ${allInputsMuted && discordOutputsMuted ? "is-active" : ""}`.trim()} type="button" disabled={!inputAudioDevices.length && !discordOutputSessions.length} on:click={toggleDiscordPrivacyMode}>{(allInputsMuted && discordOutputsMuted) ? "Restore Discord privacy" : "Mute mic + Discord"}</button>
            </div>
          </section>

          <section class="settings-section">
            <div class="section-title">Inputs</div>
            <div class="settings-subtitle">Current capture devices and mute state.</div>
            {#if inputAudioDevices.length}
              <div class="device-chip-row">
                {#each inputAudioDevices as inputDevice}
                  <div class={`device-chip ${inputDevice.isMuted ? "is-muted" : ""}`.trim()}>
                    <span>{inputDevice.name}</span>
                    <span>{inputDevice.isMuted ? "Muted" : `${inputDevice.volumePercent}%`}</span>
                  </div>
                {/each}
              </div>
            {:else}
              <div class="mini-card"><div class="footer-note">No active input devices detected.</div></div>
            {/if}
          </section>

          <section class="settings-section settings-section-full">
            <div class="settings-row">
              <div>
                <div class="section-title">Audio Devices & Processes</div>
                <div class="settings-subtitle">Tick exact device and app rows to show in the mixer. Multiple devices can stay visible at the same time.</div>
              </div>
              <button class="ghost-button" type="button" on:click={saveAudio}>Save targets</button>
            </div>
            <div class="device-card-list">
              {#each outputAudioDevices as device}
                <article class={`device-card ${device.isSelected ? "is-selected" : ""}`.trim()}>
                  <div class="settings-row">
                    <div>
                      <div class="device-card-title">{device.name}</div>
                      <div class="settings-hint">{device.isDefault ? "Default output" : "Output device"} · {(device.sessions?.length ?? 0)} process{(device.sessions?.length ?? 0) === 1 ? "" : "es"}</div>
                    </div>
                    <div class="settings-hint">{visibleTargetsForDevice(device.id)} visible</div>
                  </div>
                  <div class="device-master-row">
                    <div class="settings-hint">Device volume</div>
                    <div class="device-master-controls">
                      <input class="settings-range device-master-slider" type="range" min="0" max="100" step="1" value={device.master.volumePercent} on:input={(event) => onInventoryMasterInput(device.id, event)} on:change={(event) => onInventoryMasterChange(device.id, event)}>
                      <div class="settings-range-value">{device.master.volumePercent}%</div>
                      <button class={`mute-button ${device.master.isMuted ? "is-muted" : ""}`.trim()} type="button" aria-label={device.master.isMuted ? "Unmute device" : "Mute device"} on:click={() => onInventoryMasterMute(device)}>{device.master.isMuted ? "🔇" : "🔊"}</button>
                    </div>
                  </div>
                  <div class="device-chip-row">
                    <button class={`device-chip ${hasVisibleAudioTarget(device.id, device.master.name) ? "is-selected" : ""} ${device.master.isMuted ? "is-muted" : ""}`.trim()} type="button" on:click={() => toggleAudioTarget(device.id, device.master.name)}>
                      <span>{device.master.name}</span>
                      <span>{device.master.volumePercent}%</span>
                    </button>
                    {#each device.sessions ?? [] as session}
                      <button class={`device-chip ${hasVisibleAudioTarget(device.id, session.name) ? "is-selected" : ""} ${session.isMuted ? "is-muted" : ""} ${isDiscordSession(session) ? "is-discord" : ""}`.trim()} type="button" title={session.detail || session.name} on:click={() => toggleAudioTarget(device.id, session.name)}>
                        <span>{session.name}</span>
                        <span>{session.isMuted ? "Muted" : `${session.volumePercent}%`}</span>
                      </button>
                    {/each}
                  </div>
                </article>
              {/each}
            </div>
          </section>
        </div>
      {/if}

      {#if settingsSection === "layout"}
        <section class="settings-section">
          <div class="settings-row">
            <div class="section-title">Layout Studio</div>
            <button class="ghost-button" type="button" on:click={resetLayoutProfile}>Reset</button>
          </div>
          <div class="settings-subtitle">Customize snap density for the current device. Panel placement is edited directly on the dashboard and saved per screen size.</div>
          <div class="settings-form-grid">
            <label class="settings-field">
              <span class="settings-field-label">Profile</span>
              <select id="settings-layout-profile" name="settings-layout-profile" class="settings-input" bind:value={layoutEditorProfile} on:change={syncLayoutEditorFields}>
                <option value="desktop">Desktop</option>
                <option value="tablet-landscape">Tablet Landscape</option>
                <option value="phone-landscape">Phone Landscape</option>
              </select>
            </label>
            <label class="settings-field">
              <span class="settings-field-label">Snap Columns</span>
              <input id="settings-layout-columns" name="settings-layout-columns" class="settings-input" type="number" min="1" max="120" step="1" bind:value={layoutColumnsValue} on:change={applyLayoutFields}>
            </label>
            <label class="settings-field">
              <span class="settings-field-label">Snap Rows</span>
              <input id="settings-layout-rows" name="settings-layout-rows" class="settings-input" type="number" min="1" max="120" step="1" bind:value={layoutRowsValue} on:change={applyLayoutFields}>
            </label>
          </div>
        </section>
      {/if}

      {#if settingsSection === "discord"}
        <section class="settings-section">
          <div class="settings-row">
            <div class="section-title">Discord Relay</div>
            <button class="ghost-button" type="button" on:click={saveDiscord}>Apply</button>
          </div>
          <div class="settings-subtitle">The bot token stays on your hosted relay.</div>
          <label class="settings-switch">
            <input id="settings-discord-enabled" name="settings-discord-enabled" type="checkbox" bind:checked={discordForm.enabled}>
            <span>Enable Discord panel</span>
          </label>
          <div class="settings-form-grid">
            <label class="settings-field"><span class="settings-field-label">Relay URL</span><input id="settings-discord-relay-url" name="settings-discord-relay-url" class="settings-input" type="url" bind:value={discordForm.relayUrl}></label>
            <label class="settings-field"><span class="settings-field-label">Relay API key</span><input id="settings-discord-api-key" name="settings-discord-api-key" class="settings-input" type="password" autocomplete="new-password" bind:value={discordForm.apiKey}><span class="settings-hint">{settingsMask(preferences?.discord?.apiKeyHint)}</span></label>
            <label class="settings-field"><span class="settings-field-label">Guild ID</span><input id="settings-discord-guild-id" name="settings-discord-guild-id" class="settings-input" type="text" bind:value={discordForm.guildId}></label>
            <label class="settings-field"><span class="settings-field-label">Messages channel ID</span><input id="settings-discord-messages-channel-id" name="settings-discord-messages-channel-id" class="settings-input" type="text" bind:value={discordForm.messagesChannelId}></label>
            <label class="settings-field"><span class="settings-field-label">Fallback voice channel ID</span><input id="settings-discord-voice-channel-id" name="settings-discord-voice-channel-id" class="settings-input" type="text" bind:value={discordForm.voiceChannelId}></label>
            <label class="settings-field"><span class="settings-field-label">Tracked user ID</span><input id="settings-discord-tracked-user-id" name="settings-discord-tracked-user-id" class="settings-input" type="text" bind:value={discordForm.trackedUserId}></label>
            <label class="settings-field"><span class="settings-field-label">Latest messages</span><input id="settings-discord-latest-messages" name="settings-discord-latest-messages" class="settings-input" type="number" min="1" max="20" step="1" bind:value={discordForm.latestMessagesCount}></label>
            <label class="settings-field settings-field-full"><span class="settings-field-label">Favorite user IDs</span><textarea id="settings-discord-favorite-users" name="settings-discord-favorite-users" class="settings-input settings-textarea" rows="4" bind:value={discordForm.favoriteUserIds}></textarea></label>
          </div>
        </section>
      {/if}

      {#if settingsSection === "spotify"}
        <section class="settings-section">
          <div class="settings-row">
            <div class="section-title">Spotify</div>
            <div class="settings-inline-actions">
              <button class="ghost-button" type="button" on:click={saveSpotify}>Apply</button>
              <button class="ghost-button" type="button" on:click={connectSpotify}>Connect</button>
              <button class="ghost-button" type="button" on:click={disconnectSpotify}>Disconnect</button>
            </div>
          </div>
          <div class="settings-subtitle">Playback commands require an active Spotify device, and most controls require Spotify Premium.</div>
          <label class="settings-switch">
            <input id="settings-spotify-enabled" name="settings-spotify-enabled" type="checkbox" bind:checked={spotifyForm.enabled}>
            <span>Enable Spotify panel</span>
          </label>
          <div class="settings-form-grid">
            <label class="settings-field"><span class="settings-field-label">Client ID</span><input id="settings-spotify-client-id" name="settings-spotify-client-id" class="settings-input" type="text" bind:value={spotifyForm.clientId}></label>
            <label class="settings-field"><span class="settings-field-label">Redirect URI</span><input id="settings-spotify-redirect-uri" name="settings-spotify-redirect-uri" class="settings-input" type="text" readonly value={`${location.origin}/api/spotify/connect/callback`}></label>
            <label class="settings-field settings-field-full"><span class="settings-field-label">Status</span><input id="settings-spotify-status" name="settings-spotify-status" class="settings-input" type="text" readonly value={spotifyForm.status}></label>
          </div>
        </section>
      {/if}
    </div>
  </div>
  </form>
</aside>

<aside class="theme-drawer" aria-hidden={!themeOpen}>
  <form class="drawer-form" autocomplete="off" on:submit|preventDefault>
  <input class="visually-hidden" type="text" name="theme-secret-context" autocomplete="username" tabindex="-1" aria-hidden="true" value="studio-theme">
  <div class="settings-header">
    <div>
      <div class="eyebrow">Studio Theme</div>
      <div class="settings-subtitle">Presets, local wallpapers, and Pexels-powered photo or video backgrounds.</div>
    </div>
    <button class="settings-close" type="button" on:click={() => (themeOpen = false)}>✕</button>
  </div>

  <section class="settings-section">
    <div class="section-title">Preset</div>
    <label class="settings-field">
      <span class="settings-field-label">Studio palette</span>
      <select id="theme-preset" name="theme-preset" class="settings-input" bind:value={themeForm.presetId} on:change={(event) => setPreset(event.currentTarget.value)}>
        {#each presets as preset}
          <option value={preset.id}>{preset.label}</option>
        {/each}
      </select>
    </label>
  </section>

  <section class="settings-section">
    <div class="settings-row">
      <div class="section-title">Local Media</div>
      <label class="ghost-button file-button" for="theme-upload-input">Upload</label>
    </div>
    <input id="theme-upload-input" class="visually-hidden" type="file" accept="image/*,video/*" on:change={uploadThemeMedia}>
    <div class="media-grid">
      {#if localMedia.length}
        {#each localMedia as asset}
          <article class={`media-card ${(background?.source === "local" && background?.assetId === asset.id) ? "is-selected" : ""}`.trim()}>
            <div class="media-card-preview">
              {#if asset.mediaKind === "video"}
                <video src={asset.previewUrl} muted loop playsinline preload="metadata"></video>
              {:else}
                <img src={asset.previewUrl} alt={asset.name}>
              {/if}
            </div>
            <div class="media-card-body">
              <strong>{asset.name}</strong>
              <div class="footer-note">{asset.mediaKind === "video" ? "Video" : "Image"} · {asset.sizeBytes}</div>
            </div>
            <div class="media-card-actions">
              <button class="ghost-button" type="button" on:click={() => selectLocalMedia(asset)}>Apply</button>
              <button class="ghost-button danger" type="button" on:click={() => deleteLocalMedia(asset.id)}>Delete</button>
            </div>
          </article>
        {/each}
      {:else}
        <div class="mini-card"><div class="footer-note">Upload a local image or video to build a wallpaper library for Studio.</div></div>
      {/if}
    </div>
  </section>

  <section class="settings-section">
    <div class="settings-row">
      <div class="section-title">Pexels Search</div>
      <button class="ghost-button" type="button" on:click={savePexelsKey}>Save key</button>
    </div>
    <label class="settings-field">
      <span class="settings-field-label">Pexels API key</span>
      <input id="theme-pexels-api-key" name="theme-pexels-api-key" class="settings-input" type="password" autocomplete="new-password" bind:value={themeForm.pexelsApiKey}>
      <span class="settings-hint">{settingsMask(preferences?.theme?.pexelsApiKeyHint)}</span>
    </label>
    <div class="settings-form-grid">
      <label class="settings-field settings-field-full"><span class="settings-field-label">Search</span><input id="theme-pexels-search" name="theme-pexels-search" class="settings-input" type="search" bind:value={pexelsQuery} on:keydown={(event) => event.key === "Enter" && runPexelsSearch(1)}></label>
    </div>
    <div class="settings-row media-search-actions">
      <button class="ghost-button" type="button" on:click={() => runPexelsSearch(1)}>Search</button>
    </div>
    <div class="toggle-grid">
      <button class={`toggle-pill ${pexelsKind === "image" ? "is-active" : ""}`.trim()} type="button" on:click={() => { pexelsKind = "image"; runPexelsSearch(1); }}>Photos</button>
      <button class={`toggle-pill ${pexelsKind === "video" ? "is-active" : ""}`.trim()} type="button" on:click={() => { pexelsKind = "video"; runPexelsSearch(1); }}>Videos</button>
    </div>
    <div class="settings-row media-pagination">
      <button class="ghost-button" type="button" disabled={(pexelsResult?.page ?? 1) <= 1} on:click={() => runPexelsSearch((pexelsResult?.page ?? 1) - 1)}>Prev</button>
      <div class="settings-subtitle">Page {pexelsResult?.page ?? 1}</div>
      <button class="ghost-button" type="button" disabled={!pexelsResult?.nextPage} on:click={() => runPexelsSearch((pexelsResult?.page ?? 1) + 1)}>Next</button>
    </div>
    <div class="toggle-grid page-pill-grid">
      {#each themePages() as page}
        <button class={`toggle-pill ${page === (pexelsResult?.page ?? 1) ? "is-active" : ""}`.trim()} type="button" on:click={() => runPexelsSearch(page)}>{page}</button>
      {/each}
    </div>
    <div class="media-grid media-grid-search">
      {#if pexelsResult?.results?.length}
        {#each pexelsResult.results as asset}
          <article class={`media-card ${selectedPexels(asset) ? "is-selected" : ""}`.trim()}>
            <div class="media-card-preview"><img src={asset.previewUrl} alt={asset.label}></div>
            <div class="media-card-body"><strong>{asset.label}</strong><div class="footer-note">{asset.attribution}</div></div>
            <div class="media-card-actions">
              <button class="ghost-button" type="button" on:click={() => selectPexelsResult(asset)}>Apply</button>
              <a class="ghost-button" href={asset.pexelsUrl} target="_blank" rel="noreferrer">Pexels</a>
            </div>
          </article>
        {/each}
      {:else}
        <div class="mini-card"><div class="footer-note">Search Pexels for photos or videos to use as Studio backgrounds.</div></div>
      {/if}
    </div>
    <div class="warning-text">{pexelsWarning}</div>
  </section>
  </form>
</aside>
<div class="dashboard-viewport" bind:this={viewportEl}>
  <main class="dashboard" bind:this={dashboardEl} style={dashboardCssText}>
    <section role="group" aria-label="Hardware Temperatures panel" data-panel="temps" class={`panel panel-temps ${!panelView.temps?.visible ? "is-hidden" : ""} ${panelView.temps?.locked ? "is-layout-locked" : ""} ${panelView.temps?.compact ? "is-layout-compact" : ""} ${panelView.temps?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.temps?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "temps")}>
      <header class="panel-header"><span class="eyebrow">Hardware Temperatures</span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "temps", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.temps?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.temps?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("temps")}>{panelView.temps?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.temps?.tiny ? "" : "Temps"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "temps", "resize")}>◢</button>
        </div>
      {/if}
      <div class="temp-grid">
        {#each snapshot?.temps?.cards ?? [] as card}
          <article class="temp-card">
            <div class="temp-label">{card.label}</div>
            <div class="temp-main">
              <div class={`temp-value severity-${card.severity}`}>{card.value == null ? "--" : `${Math.round(card.value)}°`}</div>
              <div class="bar-track"><div class={`bar-fill ${card.severity === "warning" ? "warning" : card.severity === "danger" ? "danger" : ""}`} style={`width:${card.fillPercent}%`}></div></div>
            </div>
          </article>
        {/each}
      </div>
      <div class="warning-text">{snapshot?.temps?.warning ?? ""}</div>
    </section>

    <section role="group" aria-label="Network panel" data-panel="network" class={`panel panel-network ${!panelView.network?.visible ? "is-hidden" : ""} ${panelView.network?.locked ? "is-layout-locked" : ""} ${panelView.network?.compact ? "is-layout-compact" : ""} ${panelView.network?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.network?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "network")}>
      <header class="panel-header"><span class="eyebrow">Network</span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "network", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.network?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.network?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("network")}>{panelView.network?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.network?.tiny ? "" : "Network"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "network", "resize")}>◢</button>
        </div>
      {/if}
      <div class="metric-row">
        <div class="metric-block"><div class="metric-value">{formatNumber(snapshot?.network?.downloadMbps)}</div><div class="metric-label">Mbps down</div></div>
        <div class="metric-block"><div class="metric-value">{formatNumber(snapshot?.network?.uploadMbps)}</div><div class="metric-label">Mbps up</div></div>
      </div>
      <div class="metric-row compact">
        <div class="metric-block"><div class="metric-value small">{snapshot?.network?.pingMs == null ? "--" : `${formatNumber(snapshot.network.pingMs)}ms`}</div><div class="metric-label">Ping</div></div>
        <div class="metric-block"><div class="metric-value small">{snapshot?.network?.jitterMs == null ? "--" : `${formatNumber(snapshot.network.jitterMs)}ms`}</div><div class="metric-label">Jitter</div></div>
      </div>
      <div class="sparkline-wrap">
        <svg class="sparkline" viewBox="0 0 300 74" preserveAspectRatio="none">
          <polyline class="sparkline-line down" points={pointsFor(snapshot?.network?.downloadHistory ?? [])}></polyline>
          <polyline class="sparkline-line up" points={pointsFor(snapshot?.network?.uploadHistory ?? [])}></polyline>
        </svg>
      </div>
    </section>

    <section role="group" aria-label="Discord panel" data-panel="discord" class={`panel panel-discord ${!panelView.discord?.visible ? "is-hidden" : ""} ${!snapshot?.discord?.enabled ? "is-disabled" : ""} ${panelView.discord?.locked ? "is-layout-locked" : ""} ${panelView.discord?.compact ? "is-layout-compact" : ""} ${panelView.discord?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.discord?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "discord")}>
      <header class="panel-header"><span class="eyebrow">Discord</span><span class={`panel-state discord-state state-${snapshot?.discord?.enabled ? (snapshot.discord.connectionState === "connected" ? "connected" : "warning") : "disabled"}`}></span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "discord", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.discord?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.discord?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("discord")}>{panelView.discord?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.discord?.tiny ? "" : "Discord"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "discord", "resize")}>◢</button>
        </div>
      {/if}
      {#if snapshot?.discord?.enabled}
        {#if snapshot.discord.trackedUser}
          <div class="discord-track"><div class="discord-identity-card"><div class="discord-identity-row"><span class={`discord-dot accent-${snapshot.discord.trackedUser.accent}`}></span><strong>{snapshot.discord.trackedUser.name}</strong></div><div class="discord-subtitle">{snapshot.discord.trackedUser.activity ?? "No active rich presence"}</div></div></div>
        {/if}
        <div class="discord-sections">
          {#if snapshot.discord.voiceChannel}
            <div class="discord-block"><div class="discord-section-title">Voice</div><div class="discord-list"><div class="discord-group-note">#{snapshot.discord.voiceChannel.name}</div>{#each snapshot.discord.voiceChannel.members.filter((member) => member.id !== snapshot.discord.trackedUser?.id) as member}<div class="discord-line"><div class="discord-line-main"><span class={`discord-dot accent-${member.accent}`}></span><span class="discord-name">{member.name}</span></div><div class="discord-line-side">{member.isMuted ? "Muted" : "Live"} · {member.isDeafened ? "Deaf" : "Listen"}</div></div>{/each}</div></div>
          {/if}
          {#if snapshot.discord.favoriteUsers.filter((user) => user.id !== snapshot.discord.trackedUser?.id).length}
            <div class="discord-block"><div class="discord-section-title">Favorites</div><div class="discord-list favorites-list">{#each snapshot.discord.favoriteUsers.filter((user) => user.id !== snapshot.discord.trackedUser?.id) as user}<div class="discord-line"><div class="discord-line-main"><span class={`discord-dot accent-${user.accent}`}></span><span class="discord-name">{user.name}</span></div><div class="discord-line-side">{user.activity ?? user.status}</div></div>{/each}</div></div>
          {/if}
          {#if snapshot.discord.latestMessages?.length}
            <div class="discord-block"><div class="discord-section-title">Latest</div><div class="discord-list">{#each snapshot.discord.latestMessages as message}<div class="discord-message-row"><div class="discord-message-top"><span class="discord-name">{message.author}</span><span class="discord-line-side">{message.relativeTime}</span></div><div class="discord-message-copy">{message.content}</div></div>{/each}</div></div>
          {/if}
        </div>
      {/if}
      <div class="warning-text">{snapshot?.discord?.enabled ? (snapshot.discord.warning ?? "") : ""}</div>
    </section>

    <section role="group" aria-label="Spotify panel" data-panel="spotify" class={`panel panel-spotify ${!panelView.spotify?.visible ? "is-hidden" : ""} ${!snapshot?.spotify?.enabled ? "is-disabled" : ""} ${panelView.spotify?.locked ? "is-layout-locked" : ""} ${panelView.spotify?.compact ? "is-layout-compact" : ""} ${panelView.spotify?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.spotify?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "spotify")}>
      <header class="panel-header"><span class="eyebrow">Spotify</span><span class={`panel-state spotify-state state-${snapshot?.spotify?.enabled ? (snapshot.spotify.connectionState === "connected" ? "connected" : "warning") : "disabled"}`}></span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "spotify", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.spotify?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.spotify?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("spotify")}>{panelView.spotify?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.spotify?.tiny ? "" : "Spotify"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "spotify", "resize")}>◢</button>
        </div>
      {/if}
      <div class="spotify-card">
        {#if !snapshot?.spotify?.enabled}
          <div class="spotify-empty"></div>
        {:else if spotifyNow}
          <div class="spotify-now">
            <a class="spotify-cover-link" href={spotifyNow.trackUrl || "#"} target="_blank" rel="noreferrer noopener">{#if spotifyNow.coverUrl}<img class="spotify-cover" src={spotifyNow.coverUrl} alt={`${spotifyNow.title} album art`}>{:else}<div class="spotify-cover spotify-cover-fallback">♪</div>{/if}</a>
            <div class="spotify-meta">
              <a class="spotify-title-link" href={spotifyNow.trackUrl || "#"} target="_blank" rel="noreferrer noopener">{spotifyNow.title}</a>
              <a class="spotify-meta-link" href={spotifyNow.artistUrl || "#"} target="_blank" rel="noreferrer noopener">{spotifyNow.artist || "Unknown artist"}</a>
              <a class="spotify-meta-link" href={spotifyNow.albumUrl || "#"} target="_blank" rel="noreferrer noopener">{spotifyNow.album || "Unknown album"}</a>
              <div class="spotify-utility-row">
                <button class={`spotify-chip ${spotifyNow.isLiked ? "is-active" : ""}`.trim()} type="button" on:click={() => onSpotify(spotifyNow.isLiked ? "unlike" : "like", { itemId: spotifyNow.itemId })}>{spotifyNow.isLiked ? "♥ Liked" : "♡ Like"}</button>
                <button class="spotify-chip" type="button" on:click={() => onSpotify("copy-link", { url: spotifyNow.trackUrl })}>Copy Link</button>
              </div>
            </div>
          </div>
          <div class="spotify-controls">
            <button class="spotify-control-button" type="button" on:click={() => onSpotify("previous")}>⏮</button>
            <button class="spotify-control-button is-primary" type="button" on:click={() => onSpotify(spotifyNow.isPlaying ? "pause" : "play")}>{spotifyNow.isPlaying ? "⏸" : "▶"}</button>
            <button class="spotify-control-button" type="button" on:click={() => onSpotify("next")}>⏭</button>
            <button class={`spotify-control-button ${spotifyNow.shuffleEnabled ? "is-active" : ""}`.trim()} type="button" on:click={() => onSpotify("shuffle", { value: spotifyNow.shuffleEnabled ? 0 : 1 })}>⇄</button>
            <button class={`spotify-control-button ${spotifyNow.repeatState !== "off" ? "is-active" : ""}`.trim()} type="button" on:click={() => onSpotify("repeat", { repeatState: nextRepeatState(spotifyNow.repeatState) })}>{spotifyNow.repeatState === "track" ? "↻1" : "↻"}</button>
          </div>
          <div class="spotify-progress-block"><div class="spotify-progress-row"><span class="footer-note">{formatDuration(shownSpotifyProgress)}</span><span class="footer-note">{formatDuration(spotifyNow.durationMs)}</span></div><input id="spotify-progress-slider" name="spotify-progress-slider" class="slider spotify-progress-slider" type="range" min="0" max={Math.max(spotifyNow.durationMs, 1)} step="1000" value={shownSpotifyProgress} on:input={onSeekInput} on:change={onSeekChange}></div>
          <div class="spotify-volume-row"><span class="footer-note spotify-device-name">{spotifyNow.deviceName || "No active device"}</span><div class="spotify-volume-control"><input id="spotify-volume-slider" name="spotify-volume-slider" class="slider spotify-volume-slider" type="range" min="0" max="100" step="1" value={shownSpotifyVolume} disabled={!spotifyNow.supportsVolume} on:input={onVolumeInput} on:change={onVolumeChange}><span class="metric-value small">{shownSpotifyVolume}%</span></div></div>
        {:else}
          <div class="spotify-empty"><div class="footer-note">{snapshot?.spotify?.warning ?? "No active Spotify playback."}</div></div>
        {/if}
      </div>
      <div class="warning-text">{spotifyNow ? (snapshot?.spotify?.warning ?? "") : ""}</div>
    </section>

    <section role="group" aria-label="Audio Mixer panel" data-panel="audio" class={`panel panel-audio ${!panelView.audio?.visible ? "is-hidden" : ""} ${panelView.audio?.locked ? "is-layout-locked" : ""} ${panelView.audio?.compact ? "is-layout-compact" : ""} ${panelView.audio?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.audio?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "audio")}>
      <header class="panel-header"><span class="eyebrow">Audio Mixer</span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "audio", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.audio?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.audio?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("audio")}>{panelView.audio?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.audio?.tiny ? "" : "Audio"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "audio", "resize")}>◢</button>
        </div>
      {/if}
      <div class={`audio-list ${(layoutMode === "tablet-landscape" || layoutMode === "phone-landscape") ? "is-grid" : ""}`.trim()}>
        {#if audioSessions.length}
          {#each audioSessions as session}
            <div class="audio-row">
              <div class="audio-copy">
                <strong class="audio-name" title={session.detail || session.name}>{session.name}</strong>
                {#if audioForm.showDeviceLabels && session.detail}
                  <span class="footer-note audio-detail">{session.detail}</span>
                {/if}
              </div>
              <div class="slider-wrap"><input id={`audio-slider-${session.id}`} name={`audio-slider-${session.id}`} data-session-id={session.id} class="slider" type="range" min="0" max="100" step="1" value={session.volumePercent} on:input={(event) => onAudioInput(session.id, event)} on:change={(event) => onAudioChange(session.id, event)}><div class="metric-value small">{session.volumePercent}%</div><button class={`mute-button ${session.isMuted ? "is-muted" : ""}`.trim()} type="button" on:click={() => onAudioMute(session)}>{session.isMuted ? "🔇" : "🔊"}</button></div>
            </div>
          {/each}
        {:else}
          <div class="mini-card"><div class="footer-note">No active audio sessions.</div></div>
        {/if}
      </div>
      <div class="warning-text">{snapshot?.audio?.warning ?? ""}</div>
    </section>

    <section role="group" aria-label="Processes panel" data-panel="processes" class={`panel panel-processes ${!panelView.processes?.visible ? "is-hidden" : ""} ${panelView.processes?.locked ? "is-layout-locked" : ""} ${panelView.processes?.compact ? "is-layout-compact" : ""} ${panelView.processes?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.processes?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "processes")}>
      <header class="panel-header"><span class="eyebrow">Processes</span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "processes", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.processes?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.processes?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("processes")}>{panelView.processes?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.processes?.tiny ? "" : "Processes"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "processes", "resize")}>◢</button>
        </div>
      {/if}
      <div id="processes-list">{#each snapshot?.processes?.topProcesses ?? [] as process}<div class="process-row"><div class="process-meta"><strong>{process.name}</strong><span class="footer-note">{process.cpuPercent.toFixed(1)}% CPU · {process.memoryMb.toFixed(0)} MB</span></div><div class="process-bar bar-track"><div class={`bar-fill ${process.cpuPercent > 60 ? "danger" : process.cpuPercent > 25 ? "warning" : ""}`} style={`width:${Math.min(process.cpuPercent, 100)}%`}></div></div></div>{/each}</div>
    </section>

    <section role="group" aria-label="System Info panel" data-panel="system" class={`panel panel-system ${!panelView.system?.visible ? "is-hidden" : ""} ${panelView.system?.locked ? "is-layout-locked" : ""} ${panelView.system?.compact ? "is-layout-compact" : ""} ${panelView.system?.tiny ? "is-layout-tiny" : ""}`.trim()} style={panelView.system?.style ?? ""} on:pointerdown={(event) => handlePanelPointerDown(event, "system")}>
      <header class="panel-header"><span class="eyebrow">System Info</span></header>
      {#if layoutEditing}
        <div class="layout-shell" role="presentation">
          <button class="layout-move-handle" type="button" aria-label="Move panel" on:pointerdown={(event) => startLayoutDrag(event, "system", "move")}>Move</button>
          <button class={`layout-lock-toggle ${panelView.system?.locked ? "is-locked" : ""}`.trim()} type="button" aria-label={panelView.system?.locked ? "Unlock panel" : "Lock panel"} on:click={() => togglePanelLock("system")}>{panelView.system?.locked ? "Locked" : "Lock"}</button>
          <div class="layout-panel-tag">{panelView.system?.tiny ? "" : "System"}</div>
          <button class="layout-resize-handle" type="button" aria-label="Resize panel" on:pointerdown={(event) => startLayoutDrag(event, "system", "resize")}>◢</button>
        </div>
      {/if}
      <dl class="system-list">{#each systemRows(snapshot?.system) as row}<dt>{row[0]}</dt><dd>{row[1] || "--"}</dd>{/each}</dl>
    </section>
  </main>
</div>
