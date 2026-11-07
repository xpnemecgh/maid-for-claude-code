(() => {
  'use strict';

  // ---- the app around the page ------------------------------------------------------------------
  // window.MaidNative (the Android app): messages with a "type" go to Maid as they are, those with
  // "native" are for the app. What comes back: Maid's messages ("type") and the app's ("native").

  const native = window.MaidNative;
  const send = (message) => { if (native) native.postMessage(JSON.stringify(message)); };
  const { THEME, themeFor, tintOutput } = window.MaidTerm;

  // ---- constants ------------------------------------------------------------------------------------

  const FONT = { min: 7, max: 22, fallback: 12 };
  const ACT_TIMEOUT_MS = 15000;
  const SWIPE_PX = 70;
  const HOLD_MS = 450;
  const REPEAT_MS = 70;

  const ICONS = {
    play: '<svg class="s16" viewBox="0 0 16 16"><path class="fill" d="M4.5 2.9v10.2c0 .6.65.96 1.15.64l7.9-5.1a.75.75 0 0 0 0-1.28l-7.9-5.1a.75.75 0 0 0-1.15.64z"/></svg>',
    resume: '<svg class="s16" viewBox="0 0 16 16" stroke-width="1.4"><path d="M2.5 8a5.5 5.5 0 1 0 1.8-4.07"/><path d="M2.4 2.2v2.6H5"/><path d="M8 5.2V8l1.9 1.2"/></svg>',
    stop: '<svg viewBox="0 0 24 24"><rect class="fill" x="6" y="6" width="12" height="12" rx="2.2"/></svg>',
    close: '<svg viewBox="0 0 24 24"><path d="m6 6 12 12M18 6 6 18"/></svg>',
    copy: '<svg viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5.5A1.5 1.5 0 0 0 14.5 4h-9A1.5 1.5 0 0 0 4 5.5v9A1.5 1.5 0 0 0 5.5 16H8"/></svg>',
    fit: '<svg viewBox="0 0 24 24"><rect x="7" y="3" width="10" height="18" rx="2"/><path d="M11 17.5h2"/></svg>',
    pc: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="1.8"/><path d="M9 20h6M12 16v4"/></svg>',
  };

  // ---- state ------------------------------------------------------------------------------------------

  const state = {
    link: { state: 'unpaired', detail: '', server: '', paired: false, locked: false },
    settings: { appLock: true, canLock: false, background: true, keepScreenOn: false, dictationSend: false, dictationBox: false, version: '' },
    // whether Maid can write out a dictation: { available, source } from its init; null: Maid predates dictation
    dictationSupport: undefined,
    entries: [],          // Maid's projects { id, path, name, group }
    groups: [],           // { name, parent, collapsed, color }
    hub: null,            // the "All projects" pseudo project
    tiles: [],            // the PC's windows, in its order
    current: null,        // the id of the window shown
    // what the terminal shows: the window, its run, whether its session runs, who has the console's size
    view: null,
    pendingShow: null,    // a window Maid is about to open (started from here)
    fontSize: clampFont(Number(readStored('fontSize')) || FONT.fallback),
    visible: true,
    pairing: false,       // the pairing screen is open although the phone is paired (to pair again)
    pairBusy: false,
    acts: new Map(),      // ref -> callback, for what Maid's page was asked to do
    refs: 0,
    booted: false,
  };

  function clampFont(size) { return Math.min(FONT.max, Math.max(FONT.min, Math.round(size * 2) / 2)); }
  function readStored(key) { try { return localStorage.getItem(key); } catch (error) { return null; } }
  function store(key, value) { try { localStorage.setItem(key, value); } catch (error) { /* only a convenience */ } }

  const $ = (id) => document.getElementById(id);
  const els = {
    pairView: $('pair-view'), mainView: $('main-view'), lockView: $('lock-view'),
    scan: $('scan-btn'), pasteLink: $('paste-link-btn'), linkBox: $('link-box'), linkInput: $('link-input'),
    linkPair: $('link-pair'), pairStatus: $('pair-status'), pairCancel: $('pair-cancel'),
    openSheet: $('open-sheet'), waitingBadge: $('waiting-badge'), title: $('title'), titleName: $('title-name'),
    titleState: $('title-state'), openMenu: $('open-menu'),
    strip: $('strip'), linkBanner: $('link-banner'), linkText: $('link-text'), linkAction: $('link-action'),
    stage: $('stage'), term: $('term'), sizeBanner: $('size-banner'), fitPhone: $('fit-phone'), toBottom: $('to-bottom'),
    exitBar: $('exit-bar'), exitText: $('exit-text'), exitLaunch: $('exit-launch'), exitResume: $('exit-resume'), exitClose: $('exit-close'),
    empty: $('empty'), emptyTitle: $('empty-title'), emptyText: $('empty-text'), emptyStart: $('empty-start'),
    keys: $('keys'), moreKeys: $('more-keys'), compose: $('compose'), message: $('message'), send: $('send'), mic: $('mic'),
    dictation: $('dictation'), dictationCancel: $('dictation-cancel'), dictationDot: $('dictation-dot'), dictationTime: $('dictation-time'),
    dictationWave: $('dictation-wave'), dictationText: $('dictation-text'), dictationAction: $('dictation-action'),
    sheet: $('sheet'), sheetServer: $('sheet-server'), sheetStatus: $('sheet-status'), closeSheet: $('close-sheet'),
    windowsHead: $('windows-head'), windowList: $('window-list'), startHead: $('start-head'), projectList: $('project-list'),
    settingLockRow: $('setting-lock-row'), settingLock: $('setting-lock'), settingBackground: $('setting-background'), settingScreen: $('setting-screen'),
    settingDictationSend: $('setting-dictation-send'), settingDictationBox: $('setting-dictation-box'),
    fontSizeText: $('font-size-text'), fontDown: $('font-down'), fontUp: $('font-up'),
    connectToggle: $('connect-toggle'), pairAgain: $('pair-again'), forget: $('forget'), version: $('version'),
    menu: $('menu'), moreMenu: $('more-menu'), unlock: $('unlock'),
    confirm: $('confirm'), confirmTitle: $('confirm-title'), confirmText: $('confirm-text'), confirmYes: $('confirm-yes'), confirmNo: $('confirm-no'),
    toast: $('toast'),
  };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function iconButton(className, svg, label) {
    const button = element('button', className);
    button.type = 'button';
    button.innerHTML = svg;
    button.setAttribute('aria-label', label);
    return button;
  }

  const connected = () => state.link.state === 'connected';
  const tileById = (id) => state.tiles.find((tile) => tile.id === id);
  const currentTile = () => (state.current ? tileById(state.current) : null);
  const isLive = (tile) => !!tile && (tile.status === 'running' || tile.status === 'starting');

  // ---- groups (as on the PC) ----------------------------------------------------------------------

  const groupKey = (name) => (name || '').trim().toLowerCase();
  const sameGroup = (a, b) => groupKey(a) === groupKey(b);
  const findGroup = (name) => state.groups.find((group) => sameGroup(group.name, name));
  const childrenOf = (parent) => state.groups.filter((group) => sameGroup(group.parent, parent));
  function lineageOf(name) {
    const chain = [];
    for (let group = findGroup(name); group && !chain.includes(group); group = findGroup(group.parent)) chain.push(group);
    return chain;
  }
  const colorOfGroup = (name) => { const group = lineageOf(name).find((candidate) => candidate.color); return (group && group.color) || ''; };
  function groupTree() {
    const tree = [];
    const visit = (parent, depth) => {
      for (const group of childrenOf(parent)) {
        tree.push({ group, depth });
        visit(group.name, depth + 1);
      }
    };
    visit('', 0);
    return tree;
  }

  // ---- the terminal ---------------------------------------------------------------------------------

  const term = new Terminal({
    allowProposedApi: true,
    fontFamily: '"Cascadia Mono", monospace',
    fontSize: state.fontSize,
    scrollback: 5000,
    theme: THEME,
    cursorBlink: false,
    // Typing goes through the message box and the keys; tapping the terminal must not bring up the keyboard.
    disableStdin: true,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  let termOpen = false;

  /** Opens the terminal once it can be measured (the main view is on the screen, the font loaded). */
  function ensureTerm() {
    if (termOpen || els.mainView.hidden || !els.term.clientWidth) return;
    termOpen = true;
    term.open(els.term);
    term.textarea.setAttribute('inputmode', 'none');
    term.textarea.readOnly = true;
    // The DOM renderer: one terminal does not need WebGL, whose glyph atlas clips letters at the
    // fractional pixel ratios phones have (seen at 2.625).
    term.onScroll(updateToBottom);
    term.onWriteParsed(updateToBottom);
    layout();
  }

  /** How many columns and rows fit at the chosen text size. */
  function phoneDims() {
    if (!termOpen) return { cols: 60, rows: 30 };
    if (term.options.fontSize !== state.fontSize) term.options.fontSize = state.fontSize;
    const dims = fit.proposeDimensions();
    if (!dims || !Number.isFinite(dims.cols) || !Number.isFinite(dims.rows)) return { cols: 60, rows: 30 };
    return { cols: Math.max(20, dims.cols), rows: Math.max(5, dims.rows) };
  }

  /**
   * Sizes the terminal: at the phone's own size when the console has it (and tells Maid when that
   * changed: keyboard, rotation, text size), otherwise at the console's size with the text made
   * smaller until it fits.
   */
  function layout() {
    if (!termOpen) return;
    const view = state.view;
    const dims = phoneDims();
    if (view && view.owner === 'phone') {
      if (dims.cols !== term.cols || dims.rows !== term.rows) term.resize(dims.cols, dims.rows);
      if (dims.cols !== view.cols || dims.rows !== view.rows) {
        view.cols = dims.cols;
        view.rows = dims.rows;
        if (connected()) send({ type: 'resize', id: view.id, cols: dims.cols, rows: dims.rows });
      }
    } else if (view && view.cols && view.rows) {
      const scale = Math.min(1, dims.cols / view.cols, dims.rows / view.rows);
      const size = Math.max(4, Math.floor(state.fontSize * scale * 2) / 2);
      if (term.options.fontSize !== size) term.options.fontSize = size;
      if (term.cols !== view.cols || term.rows !== view.rows) term.resize(view.cols, view.rows);
    } else if (!view) {
      if (dims.cols !== term.cols || dims.rows !== term.rows) term.resize(dims.cols, dims.rows);
    }
    updateToBottom();
  }

  let layoutTimer = 0;
  new ResizeObserver(() => {
    clearTimeout(layoutTimer);
    layoutTimer = setTimeout(() => { ensureTerm(); layout(); }, 60);
  }).observe(els.term);

  function write(data) {
    term.write(tintOutput(state.view, data));
  }

  function updateToBottom() {
    if (!termOpen) return;
    const buffer = term.buffer.active;
    els.toBottom.hidden = buffer.viewportY >= buffer.baseY;
  }

  /** The text on the screen (what "Copy screen" copies). */
  function screenText() {
    const buffer = term.buffer.active;
    const lines = [];
    for (let y = buffer.viewportY; y < buffer.viewportY + term.rows; y++) {
      const line = buffer.getLine(y);
      lines.push(line ? line.translateToString(true) : '');
    }
    return lines.join('\n').replace(/\s+$/, '');
  }

  function setFontSize(size) {
    state.fontSize = clampFont(size);
    store('fontSize', String(state.fontSize));
    layout();
    renderSettings();
  }

  // ---- showing a window ---------------------------------------------------------------------------

  /** Shows one of the PC's windows: Maid sends what it wrote so far, then the live output, sized for the phone. */
  function show(id, { force = false } = {}) {
    const tile = tileById(id);
    if (!tile) return;
    if (!force && state.view && state.view.id === id) {
      render();
      return;
    }
    state.current = id;
    store('current', id);
    state.view = { id, run: -1, live: false, ready: false, owner: 'none', cols: 0, rows: 0, color: tile.color || '', tintCarry: '' };
    term.reset();
    term.options.theme = themeFor(state.view.color);
    const dims = phoneDims();
    send({ native: 'viewing', id });
    if (connected()) send({ type: 'view', id, cols: dims.cols, rows: dims.rows });
    render();
    const chip = els.strip.querySelector(`[data-id="${CSS.escape(id)}"]`);
    if (chip) chip.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  }

  /** The next (or previous) window, as on the PC: Ctrl+Tab. */
  function cycle(step) {
    if (!state.tiles.length) return;
    const index = state.tiles.findIndex((tile) => tile.id === state.current);
    const next = state.tiles[(index + step + state.tiles.length) % state.tiles.length];
    if (next && next.id !== state.current) show(next.id);
  }

  function input(data) {
    const view = state.view;
    if (!view || !view.live || !connected()) return false;
    send({ type: 'input', id: view.id, data });
    return true;
  }

  /** Text from the message box: as typed (pasted when it has several lines), then Enter unless [enter] is false. */
  function sendText(text, enter) {
    const view = state.view;
    if (!view || !view.live || !connected()) {
      toast(connected() ? 'This window\'s session is not running.' : 'Not connected to Maid.', true);
      return false;
    }
    if (text) {
      const prepared = text.replace(/\r?\n/g, '\r');
      const lines = prepared.includes('\r');
      input(lines && term.modes.bracketedPasteMode ? `\x1b[200~${prepared}\x1b[201~` : lines ? prepared.replace(/\r/g, '\x1b\r') : prepared);
    }
    if (enter) setTimeout(() => input('\r'), text ? 40 : 0);
    term.scrollToBottom();
    return true;
  }

  // ---- asking Maid's page to act ------------------------------------------------------------------

  function act(action, fields, done) {
    if (!connected()) {
      toast('Not connected to Maid.', true);
      return;
    }
    const ref = `r${++state.refs}`;
    const timer = setTimeout(() => {
      state.acts.delete(ref);
      toast('Maid did not answer.', true);
    }, ACT_TIMEOUT_MS);
    state.acts.set(ref, (result) => {
      clearTimeout(timer);
      if (result.error) toast(result.error, true);
      else if (done) done(result);
    });
    send({ type: 'act', ref, action, ...fields });
  }

  function launchProject(entryId, { resume = false } = {}) {
    act('launchEntry', { entry: entryId, resume }, (result) => {
      closeSheet();
      if (!result.id) return;
      if (tileById(result.id)) show(result.id, { force: true });
      else state.pendingShow = result.id;
    });
  }

  // ---- rendering ----------------------------------------------------------------------------------

  function describe(tile) {
    switch (tile.status) {
      case 'starting': return 'Starting…';
      case 'running':
        if (tile.stopping) return 'Stopping…';
        if (tile.waiting) return 'Waiting for you';
        return tile.busy ? 'Working…' : 'Running';
      case 'exited':
        if (tile.stopped) return 'Stopped';
        return tile.exitCode === 0 ? 'Ended' : `Exited (${tile.exitCode})`;
      case 'failed': return 'Failed to start';
      default: return '';
    }
  }

  function flagsOf(tile, node) {
    const running = tile.status === 'running';
    node.classList.toggle('is-starting', tile.status === 'starting');
    node.classList.toggle('is-running', running);
    node.classList.toggle('is-busy', running && tile.busy && !tile.waiting);
    node.classList.toggle('is-waiting', running && tile.waiting);
    node.classList.toggle('is-failed', !!tile.failed);
  }

  function render() {
    if (!state.booted) return;
    document.body.classList.remove('booting');
    const link = state.link;
    const pairing = !link.paired || state.pairing;
    els.pairView.hidden = !pairing;
    els.mainView.hidden = pairing;
    els.lockView.hidden = !link.locked;
    els.pairCancel.hidden = !link.paired || state.pairBusy;
    if (pairing) return;
    ensureTerm();
    renderBanner();
    renderTitle();
    renderStrip();
    renderStage();
    if (!els.sheet.hidden) renderSheet();
  }

  function renderBanner() {
    const link = state.link;
    const server = link.server || 'the PC';
    let text = '';
    let action = '';
    let connecting = false;
    switch (link.state) {
      case 'connecting': case 'pairing':
        text = `Connecting to ${server}…`;
        connecting = true;
        break;
      case 'offline':
        text = link.detail || `Can't reach ${server}.`;
        action = 'Retry';
        break;
      case 'stopped':
        text = 'Disconnected.';
        action = 'Connect';
        break;
      case 'denied':
        text = link.detail || 'Maid does not accept this phone.';
        action = 'Pair again';
        break;
    }
    els.linkBanner.hidden = !text;
    els.linkBanner.classList.toggle('is-connecting', connecting);
    els.linkText.textContent = text;
    els.linkAction.textContent = action;
  }

  function renderTitle() {
    const tile = currentTile();
    for (const name of ['is-starting', 'is-running', 'is-busy', 'is-waiting', 'is-failed']) els.title.classList.remove(name);
    if (!tile) {
      els.titleName.textContent = state.link.server || 'Maid';
      els.titleState.textContent = connected() ? `${state.tiles.length ? '' : 'No windows open'}` : state.link.state === 'connected' ? '' : 'Not connected';
    } else {
      flagsOf(tile, els.title);
      els.titleName.textContent = tile.name;
      els.titleState.textContent = [describe(tile), tile.title].filter(Boolean).join(' · ');
    }
    const waiting = state.tiles.filter((other) => other.waiting && other.id !== state.current).length;
    els.waitingBadge.hidden = !waiting;
    els.waitingBadge.textContent = String(waiting);
  }

  function renderStrip() {
    const chips = state.tiles.map((tile) => {
      const chip = element('button', 'chip');
      chip.type = 'button';
      chip.dataset.id = tile.id;
      flagsOf(tile, chip);
      chip.classList.toggle('is-current', tile.id === state.current);
      chip.append(element('span', 'dot'));
      chip.append(element('span', 'name', tile.name));
      if (tile.color) {
        const mark = element('span', 'group-mark');
        mark.style.background = tile.color;
        chip.prepend(mark);
      }
      return chip;
    });
    els.strip.replaceChildren(...chips);
  }

  function renderStage() {
    const tile = currentTile();
    const view = state.view;
    els.empty.hidden = !!tile;
    if (!tile) {
      els.emptyTitle.textContent = connected() ? 'No windows open' : state.link.state === 'connecting' ? 'Connecting…' : 'Not connected';
      els.emptyText.textContent = connected()
        ? `Nothing runs on ${state.link.server || 'the PC'} right now. Start a session in one of your projects.`
        : state.link.detail || '';
      els.emptyStart.hidden = !connected();
    }
    const ended = !!tile && !isLive(tile);
    els.exitBar.hidden = !ended;
    if (ended) {
      els.exitBar.classList.toggle('is-failed', !!tile.failed);
      els.exitText.textContent = tile.status === 'failed' ? (tile.message || 'The session could not be started.')
        : `${tile.stopped ? 'Session stopped.' : `Session ended${tile.exitCode ? ` (exit code ${tile.exitCode})` : ''}.`} ${tile.message || ''}`.trim();
    }
    const live = !!view && view.live && isLive(tile);
    els.sizeBanner.hidden = !(live && view.owner === 'pc');
    els.send.disabled = !(live && connected());
    els.message.placeholder = !tile ? 'Message…' : 'Message to Claude…';
  }

  function renderSheet() {
    const link = state.link;
    els.sheetServer.textContent = link.server || 'Maid';
    els.sheetStatus.textContent = connected() ? 'Connected' : link.detail || 'Not connected';
    els.sheetStatus.classList.toggle('good', connected());

    // The PC's windows
    els.windowsHead.hidden = !state.tiles.length;
    els.windowList.replaceChildren(...state.tiles.map((tile) => {
      const item = element('li', 'window');
      item.dataset.id = tile.id;
      flagsOf(tile, item);
      item.classList.toggle('is-current', tile.id === state.current);
      const bar = element('span', 'group-bar');
      if (tile.color) bar.style.background = tile.color;
      const body = element('div', 'body');
      const name = element('div', 'name');
      name.append(element('span', '', tile.name));
      body.append(name, element('div', 'detail', tile.title || tile.group || tile.path));
      item.append(bar, element('span', 'dot'), body, element('span', 'state', describe(tile)));
      return item;
    }));

    // Projects to start a session in, group by group (the hub first)
    const rows = [];
    els.startHead.hidden = !connected();
    if (connected()) {
      if (state.hub) rows.push(projectRow(state.hub, { hub: true }));
      for (const { group } of groupTree()) {
        const members = state.entries.filter((entry) => sameGroup(entry.group, group.name));
        if (!members.length) continue;
        const title = element('li', 'group-title');
        const swatch = element('span', 'swatch');
        const color = colorOfGroup(group.name);
        if (color) swatch.style.background = color;
        title.append(swatch, lineageOf(group.name).reverse().map((each) => each.name).join(' / '));
        rows.push(title);
        for (const entry of members) rows.push(projectRow(entry));
      }
    }
    els.projectList.replaceChildren(...rows);
    renderSettings();
  }

  function projectRow(entry, { hub = false } = {}) {
    const row = element('li', 'project');
    row.classList.toggle('is-hub', hub);
    row.dataset.entry = entry.id;
    const body = element('div', 'body');
    body.append(element('div', 'name', hub ? 'All projects' : entry.name), element('div', 'detail', entry.path));
    row.append(body);
    const open = state.tiles.filter((tile) => tile.entry === entry.id && isLive(tile)).length;
    if (open) row.append(element('span', 'count', String(open)));
    row.append(iconButton('launch', ICONS.play, hub ? 'Start a session about all projects' : 'Start Claude Code'));
    if (!hub) {
      row.append(iconButton('resume', ICONS.resume, 'Resume the last conversation'));
    }
    return row;
  }

  function renderSettings() {
    const settings = state.settings;
    els.settingLockRow.hidden = !settings.canLock;
    els.settingLock.checked = !!settings.appLock;
    els.settingBackground.checked = !!settings.background;
    els.settingScreen.checked = !!settings.keepScreenOn;
    els.settingDictationSend.checked = !!settings.dictationSend;
    els.settingDictationBox.checked = !!settings.dictationBox;
    els.fontSizeText.textContent = `${state.fontSize} px (pinch the terminal to change it)`;
    els.version.textContent = `Maid for Android ${settings.version || ''}`.trim();
    // The app's own version, in the title bar and on the pairing screen.
    for (const tag of document.querySelectorAll('.app-version')) {
      tag.textContent = settings.version ? `v${settings.version}` : '';
      tag.hidden = !settings.version;
    }
    els.connectToggle.textContent = state.link.state === 'stopped' || state.link.state === 'denied' ? 'Connect' : 'Disconnect';
  }

  function openSheet({ start = false } = {}) {
    closeMenus();
    els.sheet.hidden = false;
    renderSheet();
    const body = els.sheet.querySelector('.sheet-body');
    body.scrollTop = 0;
    if (start) els.startHead.scrollIntoView({ block: 'start' });
  }

  function closeSheet() {
    els.sheet.hidden = true;
  }

  // ---- menus --------------------------------------------------------------------------------------

  function menuItem(svg, label, onClick, className = '') {
    const button = element('button', className);
    button.type = 'button';
    button.innerHTML = svg || '';
    button.append(label);
    button.addEventListener('click', () => { closeMenus(); onClick(); });
    return button;
  }

  function placeMenu(menu, anchor, { above = false } = {}) {
    menu.hidden = false;
    const rect = anchor.getBoundingClientRect();
    const width = menu.offsetWidth;
    menu.style.left = `${Math.max(8, Math.min(window.innerWidth - width - 8, rect.right - width))}px`;
    menu.style.top = above ? `${Math.max(8, rect.top - menu.offsetHeight - 6)}px` : `${rect.bottom + 4}px`;
  }

  function closeMenus() {
    els.menu.hidden = true;
    els.moreMenu.hidden = true;
  }

  function openWindowMenu() {
    const tile = currentTile();
    const view = state.view;
    const items = [];
    if (tile && tile.status === 'running') {
      if (view && view.owner === 'pc') items.push(menuItem(ICONS.fit, 'Fit to the phone', () => send({ type: 'claim', id: tile.id })));
      if (view && view.owner === 'phone') items.push(menuItem(ICONS.pc, 'Give it the PC\'s size', () => send({ type: 'yield', id: tile.id })));
    }
    if (tile) items.push(menuItem(ICONS.copy, 'Copy the screen', () => {
      send({ native: 'copy', text: screenText() });
      toast('Copied the screen.');
    }));
    if (tile && tile.status === 'running') {
      items.push(element('hr'));
      items.push(menuItem(ICONS.stop, 'Stop the session', async () => {
        if (await confirmBox(`Stop ${tile.name}?`, 'The session ends; its conversation is saved to CLAUDE.md as usual.', 'Stop')) act('stop', { id: tile.id });
      }, 'danger'));
    }
    if (tile) items.push(menuItem(ICONS.close, 'Close the window', async () => {
      if (isLive(tile) && !await confirmBox(`Close ${tile.name}?`, 'Its session is still running; closing the window stops it.', 'Close')) return;
      act('close', { id: tile.id });
    }, isLive(tile) ? 'danger' : ''));
    if (!items.length) return;
    els.menu.replaceChildren(...items);
    placeMenu(els.menu, els.openMenu);
  }

  const MORE_KEYS = [
    ['^D', '\x04'], ['^R', '\x12'], ['^O', '\x0f'], ['^T', '\x14'],
    ['^B', '\x02'], ['^L', '\x0c'], ['^Z', '\x1a'], ['^U', '\x15'],
    ['Home', '\x1b[H'], ['End', '\x1b[F'], ['PgUp', '\x1b[5~'], ['PgDn', '\x1b[6~'],
    ['Esc Esc', 'esc-esc', 'wide'], ['Paste', 'paste', 'wide'],
  ];

  function openMoreKeys() {
    els.moreMenu.replaceChildren(...MORE_KEYS.map(([label, data, wide]) => {
      const button = element('button', wide || '', label);
      button.type = 'button';
      button.addEventListener('click', () => {
        closeMenus();
        send({ native: 'haptic' });
        if (data === 'paste') send({ native: 'paste' });
        else if (data === 'esc-esc') { input('\x1b'); setTimeout(() => input('\x1b'), 120); }
        else input(data);
      });
      return button;
    }));
    els.moreMenu.style.width = `${Math.min(window.innerWidth - 16, 340)}px`;
    placeMenu(els.moreMenu, els.moreKeys, { above: true });
  }

  // ---- confirm and toast ------------------------------------------------------------------------------

  let confirmResolve = null;

  function confirmBox(title, text, action) {
    els.confirmTitle.textContent = title;
    els.confirmText.textContent = text;
    els.confirmYes.textContent = action;
    els.confirm.hidden = false;
    return new Promise((resolve) => { confirmResolve = resolve; });
  }

  function closeConfirm(answer) {
    els.confirm.hidden = true;
    if (confirmResolve) confirmResolve(answer);
    confirmResolve = null;
  }

  let toastTimer = 0;

  function toast(text, error = false) {
    clearTimeout(toastTimer);
    els.toast.textContent = text;
    els.toast.classList.toggle('error', error);
    els.toast.hidden = false;
    toastTimer = setTimeout(() => { els.toast.hidden = true; }, error ? 5000 : 2500);
  }

  // ---- input: keys, message box, gestures ------------------------------------------------------------

  const cursorKey = (letter) => (term.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[') + letter;
  const KEYS = {
    esc: () => '\x1b', tab: () => '\t', 'shift-tab': () => '\x1b[Z', enter: () => '\r', backspace: () => '\x7f', 'ctrl-c': () => '\x03',
    up: () => cursorKey('A'), down: () => cursorKey('B'), right: () => cursorKey('C'), left: () => cursorKey('D'),
  };
  const REPEATING = new Set(['up', 'down', 'left', 'right', 'backspace']);

  let repeat = { timer: 0, interval: 0 };
  function stopRepeat() {
    clearTimeout(repeat.timer);
    clearInterval(repeat.interval);
  }

  // Keys fire on touch down (a terminal key should not wait for the finger to lift); held arrows and ⌫ repeat.
  els.keys.addEventListener('pointerdown', (event) => {
    const button = event.target.closest('button[data-key]');
    if (!button) return;
    event.preventDefault();
    const key = button.dataset.key;
    const press = () => input(KEYS[key]());
    send({ native: 'haptic' });
    press();
    if (REPEATING.has(key)) {
      stopRepeat();
      repeat.timer = setTimeout(() => { repeat.interval = setInterval(press, REPEAT_MS); }, HOLD_MS);
    }
  });
  for (const type of ['pointerup', 'pointercancel', 'pointerleave']) els.keys.addEventListener(type, stopRepeat);
  els.moreKeys.addEventListener('click', () => (els.moreMenu.hidden ? openMoreKeys() : closeMenus()));

  function growMessage() {
    els.message.style.height = 'auto';
    els.message.style.height = `${Math.min(els.message.scrollHeight + 2, 140)}px`;
  }
  els.message.addEventListener('input', growMessage);

  // Send: the text and Enter. Held: the text alone (e.g. to answer a prompt that takes a key).
  let holdTimer = 0;
  let held = false;
  els.send.addEventListener('pointerdown', () => {
    held = false;
    holdTimer = setTimeout(() => {
      held = true;
      els.send.classList.add('no-enter');
      send({ native: 'haptic' });
    }, HOLD_MS);
  });
  for (const type of ['pointerup', 'pointercancel', 'pointerleave']) els.send.addEventListener(type, () => clearTimeout(holdTimer));
  els.compose.addEventListener('submit', (event) => {
    event.preventDefault();
    const enter = !held;
    held = false;
    els.send.classList.remove('no-enter');
    // While dictating, Send means: write it out, then send it.
    if (dictation.phase === 'recording' || dictation.phase === 'starting') {
      finishDictation({ andSend: true, enter });
      return;
    }
    if (dictation.phase === 'transcribing') {
      dictation.sendAfter = { enter };
      renderDictation();
      return;
    }
    if (dictation.phase === 'failed') {
      if (dictation.action === 'retry') {
        dictation.sendAfter = { enter };
        retryDictation();
        return;
      }
      cancelDictation();
    }
    submitMessage(enter);
  });

  /** What Send does: the message box's text, then Enter unless [enter] is false. */
  function submitMessage(enter) {
    const text = els.message.value;
    if (!enter && !text) return;
    if (sendText(text, enter)) {
      els.message.value = '';
      growMessage();
      if (!enter) toast('Sent without Enter.');
    }
  }

  // ---- dictation --------------------------------------------------------------------------------------
  // The app records (the microphone is the app's); Maid has ElevenLabs write the recording out with the PC's
  // key and answers here. The words go straight into the session's command line, without Enter, as with
  // Claude Code's voice mode on the PC (see intoCommandLine for when they wait in the message box instead),
  // or on with Enter when Send was pressed while dictating (or "Send dictation right away" is on). Tap the
  // microphone to start and again to stop, or hold it while talking.

  const DICTATION = { answerMs: 100000, endingMs: 270000, bars: 48 };
  const dictation = { phase: 'idle', ref: '', count: 0, window: null, sendAfter: null, action: '', timer: 0, levels: [] };
  const waveBars = Array.from({ length: DICTATION.bars }, () => document.createElement('i'));
  els.dictationWave.append(...waveBars);

  /** idle, starting (asked the app), recording, transcribing (Maid has it) or failed (action: 'retry' | 'settings' | ''). */
  function setDictation(phase, { action = '' } = {}) {
    if (phase !== 'transcribing') clearTimeout(dictation.timer);
    if (phase === 'idle') dictation.sendAfter = null;
    if (phase === 'starting') resetWave();
    dictation.phase = phase;
    dictation.action = action;
    renderDictation();
  }

  function renderDictation() {
    const phase = dictation.phase;
    const recording = phase === 'starting' || phase === 'recording';
    const shown = phase !== 'idle';
    els.dictation.hidden = !shown;
    els.message.hidden = shown;
    els.dictation.className = `dictation is-${phase}`;
    els.mic.classList.toggle('is-recording', recording);
    els.mic.classList.toggle('is-transcribing', phase === 'transcribing');
    els.mic.setAttribute('aria-label', recording ? 'Stop and write it out' : 'Dictate (tap, or hold while you talk)');
    els.dictationDot.hidden = !recording;
    els.dictationTime.hidden = !recording;
    els.dictationWave.hidden = !recording;
    els.dictationText.hidden = recording;
    els.dictationText.textContent = phase === 'transcribing' ? (dictation.sendAfter ? 'Transcribing, then sending…' : 'Transcribing…')
      : phase === 'failed' ? (dictation.action === 'settings' ? 'No access to the microphone' : 'Not transcribed') : '';
    els.dictationAction.hidden = phase !== 'failed' || !dictation.action;
    els.dictationAction.textContent = dictation.action === 'settings' ? 'Settings' : 'Try again';
    if (!shown) growMessage();
  }

  function resetWave() {
    dictation.levels = [];
    for (const bar of waveBars) bar.style.transform = '';
    els.dictationTime.textContent = '0:00';
    els.dictationTime.classList.remove('is-ending');
  }

  function showLevel(level, ms) {
    dictation.levels.push(Number(level) || 0);
    if (dictation.levels.length > DICTATION.bars) dictation.levels.shift();
    const offset = DICTATION.bars - dictation.levels.length;
    waveBars.forEach((bar, index) => {
      const value = index < offset ? 0 : dictation.levels[index - offset];
      bar.style.transform = `scaleY(${Math.max(0.08, value).toFixed(3)})`;
    });
    const seconds = Math.floor((Number(ms) || 0) / 1000);
    els.dictationTime.textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
    els.dictationTime.classList.toggle('is-ending', ms >= DICTATION.endingMs);
  }

  const newDictationRef = () => `d${Date.now().toString(36)}-${++dictation.count}`;

  /**
   * Whether the words can be typed into the session as they are: never into a window other than the one they
   * were spoken for, or unseen. Text waiting in the message box comes first, so they join it there.
   */
  const intoCommandLine = () => !state.settings.dictationBox && !els.message.value.trim() && state.visible
    && !!state.view && state.view.id === dictation.window && state.view.live && connected();

  function startDictation() {
    if (!native) return false;
    if (state.dictationSupport === null) {
      toast('Maid on the PC is older than this app: update it and start it again to dictate.', true);
      return false;
    }
    if (state.dictationSupport && !state.dictationSupport.available) {
      toast('Maid on the PC has no ElevenLabs API key: set ELEVENLABS_API_KEY there and start Maid again.', true);
      return false;
    }
    dictation.ref = newDictationRef();
    dictation.window = state.current;
    dictation.sendAfter = null;
    setDictation('starting');
    send({ native: 'haptic' });
    send({ native: 'dictate', op: 'start', ref: dictation.ref });
    return true;
  }

  /** Ends the recording; Maid writes it out. [andSend]: then the message box goes as Send would send it. */
  function finishDictation({ andSend = false, enter = true } = {}) {
    if (dictation.phase === 'starting') {
      cancelDictation();
      return;
    }
    if (dictation.phase !== 'recording') return;
    dictation.sendAfter = andSend ? { enter } : null;
    send({ native: 'haptic' });
    send({ native: 'dictate', op: 'stop' });
  }

  function cancelDictation() {
    if (dictation.phase === 'idle') return;
    dictation.ref = '';
    send({ native: 'dictate', op: 'cancel' });
    setDictation('idle');
  }

  function waitForWords() {
    clearTimeout(dictation.timer);
    dictation.timer = setTimeout(() => {
      if (dictation.phase !== 'transcribing') return;
      setDictation('failed', { action: 'retry' });
      toast('Maid did not answer with the words.', true);
    }, DICTATION.answerMs);
  }

  // Tap: starts, and the next tap stops. Held past HOLD_MS: records while held (push to talk).
  let micPress = null;
  els.mic.addEventListener('pointerdown', (event) => {
    event.preventDefault();
    const phase = dictation.phase;
    if (phase === 'idle' || phase === 'failed') {
      if (phase === 'failed') cancelDictation();
      micPress = startDictation() ? { at: performance.now(), started: true } : null;
    } else if (phase === 'starting' || phase === 'recording') {
      micPress = { at: performance.now(), started: false };
    } else {
      micPress = null;
    }
  });
  els.mic.addEventListener('pointerup', () => {
    const press = micPress;
    micPress = null;
    if (!press) return;
    if (!press.started || performance.now() - press.at >= HOLD_MS) finishDictation();
  });
  els.mic.addEventListener('pointercancel', () => { micPress = null; });
  els.dictationCancel.addEventListener('click', cancelDictation);
  els.dictationAction.addEventListener('click', () => {
    if (dictation.action === 'settings') {
      send({ native: 'dictate', op: 'appSettings' });
      setDictation('idle');
      return;
    }
    retryDictation();
  });

  /** The kept recording goes to Maid once more (the last try failed). */
  function retryDictation() {
    dictation.ref = newDictationRef();
    setDictation('transcribing');
    waitForWords();
    send({ native: 'dictate', op: 'resend', ref: dictation.ref });
  }

  // The terminal: swipe sideways for the next window, drag to scroll, pinch for the text size.
  const gesture = { mode: '', x0: 0, y0: 0, y: 0, at: 0, carry: 0, speed: 0, distance0: 0, size0: 0, raf: 0 };
  const cellHeight = () => {
    const screen = els.term.querySelector('.xterm-screen');
    return screen && term.rows ? screen.getBoundingClientRect().height / term.rows : 16;
  };
  const spread = (touches) => Math.hypot(touches[0].clientX - touches[1].clientX, touches[0].clientY - touches[1].clientY);

  function scrollBy(pixels) {
    gesture.carry += pixels;
    const cell = cellHeight();
    const lines = Math.trunc(gesture.carry / cell);
    if (lines) {
      term.scrollLines(lines);
      gesture.carry -= lines * cell;
    }
  }

  els.stage.addEventListener('touchstart', (event) => {
    if (!event.target.closest('#term')) return;
    event.preventDefault();
    cancelAnimationFrame(gesture.raf);
    if (event.touches.length === 2) {
      Object.assign(gesture, { mode: 'pinch', distance0: spread(event.touches), size0: state.fontSize });
      return;
    }
    const touch = event.touches[0];
    Object.assign(gesture, { mode: 'pending', x0: touch.clientX, y0: touch.clientY, y: touch.clientY, at: performance.now(), carry: 0, speed: 0 });
  }, { passive: false, capture: true });

  els.stage.addEventListener('touchmove', (event) => {
    if (!gesture.mode) return;
    event.preventDefault();
    if (gesture.mode === 'pinch') {
      if (event.touches.length < 2) return;
      const size = clampFont(gesture.size0 * spread(event.touches) / gesture.distance0);
      if (size !== term.options.fontSize) term.options.fontSize = size;
      gesture.size = size;
      return;
    }
    const touch = event.touches[0];
    const dx = touch.clientX - gesture.x0;
    const dy = touch.clientY - gesture.y0;
    if (gesture.mode === 'pending') {
      if (Math.abs(dx) > 14 && Math.abs(dx) > Math.abs(dy) * 1.5) gesture.mode = 'swipe';
      else if (Math.abs(dy) > 8) gesture.mode = 'scroll';
      else return;
    }
    if (gesture.mode === 'scroll') {
      const now = performance.now();
      const step = gesture.y - touch.clientY;
      gesture.speed = step / Math.max(1, now - gesture.at);
      gesture.at = now;
      gesture.y = touch.clientY;
      scrollBy(step);
    } else if (gesture.mode === 'swipe') {
      els.term.style.transform = `translateX(${dx * 0.35}px)`;
      els.term.style.opacity = String(1 - Math.min(0.5, Math.abs(dx) / 600));
    }
  }, { passive: false, capture: true });

  els.stage.addEventListener('touchend', (event) => {
    const mode = gesture.mode;
    gesture.mode = '';
    if (mode === 'pinch') {
      if (gesture.size) setFontSize(gesture.size);
      gesture.size = 0;
    } else if (mode === 'swipe') {
      const dx = event.changedTouches[0].clientX - gesture.x0;
      els.term.style.transform = '';
      els.term.style.opacity = '';
      if (Math.abs(dx) > SWIPE_PX) cycle(dx < 0 ? 1 : -1);
    } else if (mode === 'scroll' && Math.abs(gesture.speed) > 0.15) {
      // Fling: keeps scrolling and slows down.
      let speed = gesture.speed;
      let last = performance.now();
      const step = (now) => {
        scrollBy(speed * (now - last));
        last = now;
        speed *= 0.94;
        if (Math.abs(speed) > 0.02) gesture.raf = requestAnimationFrame(step);
      };
      gesture.raf = requestAnimationFrame(step);
    }
  }, { capture: true });

  // ---- buttons ------------------------------------------------------------------------------------

  els.scan.addEventListener('click', () => {
    setPairStatus('');
    send({ native: 'scan' });
  });
  els.pasteLink.addEventListener('click', () => {
    els.linkBox.hidden = false;
    els.pasteLink.hidden = true;
    els.linkInput.focus();
  });
  els.linkPair.addEventListener('click', () => {
    const link = els.linkInput.value.trim();
    if (!link) return;
    setPairStatus('Pairing…');
    state.pairBusy = true;
    render();
    send({ native: 'pairLink', link });
  });
  els.pairCancel.addEventListener('click', () => {
    state.pairing = false;
    setPairStatus('');
    render();
  });
  els.openSheet.addEventListener('click', () => openSheet());
  els.title.addEventListener('click', () => openSheet());
  els.closeSheet.addEventListener('click', closeSheet);
  els.openMenu.addEventListener('click', () => (els.menu.hidden ? openWindowMenu() : closeMenus()));
  els.strip.addEventListener('click', (event) => {
    const chip = event.target.closest('.chip');
    if (chip) show(chip.dataset.id);
  });
  els.linkAction.addEventListener('click', () => {
    if (state.link.state === 'denied') {
      state.pairing = true;
      render();
    } else {
      send({ native: 'connect' });
    }
  });
  els.fitPhone.addEventListener('click', () => { if (state.view) send({ type: 'claim', id: state.view.id }); });
  els.toBottom.addEventListener('click', () => term.scrollToBottom());
  els.exitLaunch.addEventListener('click', () => { if (state.current) act('relaunch', { id: state.current, resume: false }); });
  els.exitResume.addEventListener('click', () => { if (state.current) act('relaunch', { id: state.current, resume: true }); });
  els.exitClose.addEventListener('click', () => { if (state.current) act('close', { id: state.current }); });
  els.emptyStart.addEventListener('click', () => openSheet({ start: true }));
  els.windowList.addEventListener('click', (event) => {
    const item = event.target.closest('.window');
    if (!item) return;
    closeSheet();
    show(item.dataset.id);
  });
  els.projectList.addEventListener('click', (event) => {
    const row = event.target.closest('.project');
    const button = event.target.closest('button');
    if (!row || !button) return;
    send({ native: 'haptic' });
    const entry = row.dataset.entry;
    if (button.classList.contains('launch')) launchProject(entry);
    else if (button.classList.contains('resume')) launchProject(entry, { resume: true });
  });
  const saveSetting = (key, value) => send({ native: 'settings', settings: { [key]: value } });
  els.settingLock.addEventListener('change', () => saveSetting('appLock', els.settingLock.checked));
  els.settingBackground.addEventListener('change', () => saveSetting('background', els.settingBackground.checked));
  els.settingScreen.addEventListener('change', () => saveSetting('keepScreenOn', els.settingScreen.checked));
  els.settingDictationSend.addEventListener('change', () => saveSetting('dictationSend', els.settingDictationSend.checked));
  els.settingDictationBox.addEventListener('change', () => saveSetting('dictationBox', els.settingDictationBox.checked));
  els.fontDown.addEventListener('click', () => setFontSize(state.fontSize - 1));
  els.fontUp.addEventListener('click', () => setFontSize(state.fontSize + 1));
  els.connectToggle.addEventListener('click', () => {
    send({ native: state.link.state === 'stopped' || state.link.state === 'denied' ? 'connect' : 'disconnect' });
  });
  els.pairAgain.addEventListener('click', () => {
    closeSheet();
    state.pairing = true;
    render();
  });
  els.forget.addEventListener('click', async () => {
    if (!await confirmBox(`Forget ${state.link.server || 'the PC'}?`, 'This phone\'s key is deleted. To connect again, pair it anew from Maid on the PC.', 'Forget')) return;
    closeSheet();
    send({ native: 'forget' });
  });
  els.unlock.addEventListener('click', () => send({ native: 'unlock' }));
  els.confirmYes.addEventListener('click', () => closeConfirm(true));
  els.confirmNo.addEventListener('click', () => closeConfirm(false));
  els.confirm.addEventListener('click', (event) => { if (event.target === els.confirm) closeConfirm(false); });
  document.addEventListener('pointerdown', (event) => {
    if (!event.target.closest('.menu') && !event.target.closest('#open-menu') && !event.target.closest('#more-keys')) closeMenus();
  }, true);

  function setPairStatus(text, kind = '') {
    els.pairStatus.textContent = text;
    els.pairStatus.hidden = !text;
    els.pairStatus.className = kind;
  }

  /** Android's back: closes what is open on top; false when there is nothing (the app goes to the background). */
  window.MaidApp = {
    back() {
      if (!els.confirm.hidden) { closeConfirm(false); return true; }
      if (!els.menu.hidden || !els.moreMenu.hidden) { closeMenus(); return true; }
      if (!els.sheet.hidden) { closeSheet(); return true; }
      if (dictation.phase !== 'idle') { cancelDictation(); return true; }
      if (state.pairing && state.link.paired) { state.pairing = false; render(); return true; }
      return false;
    },
  };

  // ---- messages from Maid ---------------------------------------------------------------------------

  /** After a tile list change: the window shown is still there, or its neighbour takes its place. */
  function settleCurrent(previousTiles) {
    if (state.pendingShow && tileById(state.pendingShow)) {
      const id = state.pendingShow;
      state.pendingShow = null;
      show(id, { force: true });
      return;
    }
    if (state.current && tileById(state.current)) return;
    if (!state.tiles.length) {
      if (state.current) send({ type: 'unview' });
      state.current = null;
      state.view = null;
      term.reset();
      return;
    }
    const old = previousTiles.findIndex((tile) => tile.id === state.current);
    const next = state.tiles[Math.min(Math.max(old, 0), state.tiles.length - 1)];
    show(next.id, { force: true });
  }

  const maid = {
    init(message) {
      state.entries = message.entries || [];
      state.groups = message.groups || [];
      state.hub = message.hub || null;
      state.dictationSupport = message.dictation || null;
      if (message.osBuild) term.options.windowsPty = { backend: 'conpty', buildNumber: message.osBuild };
      const previous = state.tiles;
      state.tiles = message.tiles || [];
      // (Re)connected: whatever was shown is asked for again; Maid's size goes with it.
      const wanted = state.current || readStored('current');
      if (wanted && tileById(wanted)) show(wanted, { force: true });
      else settleCurrent(previous);
      render();
    },

    list(message) {
      state.entries = message.entries || [];
      state.groups = message.groups || [];
      render();
    },

    tiles(message) {
      const previous = state.tiles;
      state.tiles = message.tiles || [];
      const tile = currentTile();
      if (tile && state.view && state.view.id === tile.id && (tile.color || '') !== state.view.color) {
        state.view.color = tile.color || '';
        term.options.theme = themeFor(state.view.color);
      }
      settleCurrent(previous);
      render();
    },

    screen(message) {
      const view = state.view;
      if (!view || message.id !== view.id) return;
      term.reset();
      view.tintCarry = '';
      view.run = message.run;
      view.live = !!message.live;
      view.ready = true;
      for (const piece of message.pieces || []) {
        if (piece.cols !== term.cols || piece.rows !== term.rows) term.resize(piece.cols, piece.rows);
        if (piece.data) write(piece.data);
      }
      if (!view.live) {
        view.owner = 'none';
        view.cols = term.cols;
        view.rows = term.rows;
        layout();
      }
      render();
    },

    sized(message) {
      const view = state.view;
      if (!view || message.id !== view.id) return;
      view.owner = message.owner;
      view.cols = message.cols;
      view.rows = message.rows;
      if (term.cols !== message.cols || term.rows !== message.rows) term.resize(message.cols, message.rows);
      layout();
      renderStage();
    },

    output(message) {
      const view = state.view;
      if (!view || message.id !== view.id || !view.ready || message.run !== view.run) return;
      write(message.data);
    },

    exited(message) {
      const view = state.view;
      if (!view || message.id !== view.id || message.run !== view.run) return;
      view.live = false;
      view.owner = 'none';
      render();
    },

    actResult(message) {
      const callback = state.acts.get(message.ref);
      if (!callback) return;
      state.acts.delete(message.ref);
      callback(message);
    },

    /** The words of a dictation: into the command line, into the message box (after what is typed there), or sent on. */
    transcribed(message) {
      if (!message.ref || message.ref !== dictation.ref || dictation.phase !== 'transcribing') return;
      const after = dictation.sendAfter;
      send({ native: 'dictate', op: 'discard' });
      setDictation('idle');
      const text = (message.text || '').trim();
      if (!text) {
        toast('Nothing was heard.');
        return;
      }
      if (!after && !state.settings.dictationSend && intoCommandLine()) {
        // The space keeps the next dictation apart from this one; Enter sends them.
        sendText(`${text} `, false);
        return;
      }
      const before = els.message.value;
      els.message.value = before + (before && !/\s$/.test(before) ? ' ' : '') + text;
      growMessage();
      if (after || state.settings.dictationSend) submitMessage(after ? after.enter : true);
    },

    transcribeFailed(message) {
      if (!message.ref || message.ref !== dictation.ref || dictation.phase !== 'transcribing') return;
      setDictation('failed', { action: 'retry' });
      toast(message.message || 'The dictation could not be transcribed.', true);
    },
  };

  // ---- messages from the app ----------------------------------------------------------------------

  const app = {
    status(message) {
      const before = state.link;
      state.link = message;
      state.booted = true;
      if (message.state === 'connected' && before.state !== 'connected') state.pairing = false;
      if (!message.paired && before.paired) {
        // Forgotten: nothing of the PC stays on the screen.
        state.tiles = [];
        state.entries = [];
        state.groups = [];
        state.current = null;
        state.view = null;
        term.reset();
        cancelDictation();
      }
      render();
    },

    settings(message) {
      state.settings = message.settings || state.settings;
      renderSettings();
    },

    pairResult(message) {
      state.pairBusy = false;
      if (message.ok) {
        state.pairing = false;
        els.linkInput.value = '';
        els.linkBox.hidden = true;
        els.pasteLink.hidden = false;
        setPairStatus('');
        toast(message.message || 'Paired.');
      } else if (!message.cancelled) {
        setPairStatus(message.message || 'Pairing did not work.', 'error');
      }
      render();
    },

    openTile(message) {
      if (tileById(message.id)) show(message.id);
      else state.pendingShow = message.id;
    },

    visible(message) {
      state.visible = !!message.visible;
      // Back on the screen: the window is asked for again (the app gave its size back when it left).
      if (state.visible && connected() && state.current && tileById(state.current)) show(state.current, { force: true });
    },

    /** The recording, as the app sees it (see Dictation.kt). */
    dictation(message) {
      if (message.ref !== dictation.ref) {
        // A dictation given up on here (e.g. while Android asked for the microphone) has no business recording.
        if (message.state === 'recording') send({ native: 'dictate', op: 'cancel' });
        return;
      }
      switch (message.state) {
        case 'recording':
          if (dictation.phase === 'starting') setDictation('recording');
          break;
        case 'level':
          if (dictation.phase === 'recording') showLevel(message.level, message.ms);
          break;
        case 'transcribing':
          setDictation('transcribing');
          waitForWords();
          if (message.reason === 'limit') toast('A dictation takes five minutes at most: transcribing what you said.');
          break;
        case 'failed':
          setDictation(message.retry || message.permission ? 'failed' : 'idle', { action: message.retry ? 'retry' : message.permission ? 'settings' : '' });
          toast(message.message || 'The dictation did not work.', true);
          break;
        case 'idle':
          setDictation('idle');
          break;
      }
    },

    pasteText(message) {
      if (message.text) sendText(message.text, false);
      else toast('The clipboard has no text.');
    },
  };

  if (native) {
    native.onmessage = (event) => {
      let message;
      try { message = JSON.parse(event.data); } catch (error) { return; }
      if (message.native) {
        const handler = app[message.native];
        if (handler) handler(message);
      } else {
        const handler = maid[message.type];
        if (handler) handler(message);
      }
    };
  }

  // The terminal's font has to be there before the terminal measures it.
  document.fonts.load(`${state.fontSize}px "Cascadia Mono"`).finally(() => {
    send({ native: 'ready' });
    if (!native) {
      // In a plain browser (layout work): show the main view with nothing connected.
      state.booted = true;
      state.link = { state: 'stopped', detail: '', server: 'PC', paired: true, locked: false };
      render();
    }
  });
})();
