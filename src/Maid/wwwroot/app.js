(() => {
  'use strict';

  // ---- host bridge ----------------------------------------------------------------------------

  const host = window.chrome && window.chrome.webview;
  const post = (message) => { if (host) host.postMessage(message); };

  // ---- constants ------------------------------------------------------------------------------

  /** The session list's width in pixels: dragged at its right edge, within these limits (Maid checks them too). */
  const SIDEBAR_WIDTH = { usual: 250, min: 160, max: 700 };

  /**
   * What xterm.js sends on its own: focus reports (Claude Code asks for them), cursor position, device attributes,
   * mode and colour replies. They aren't somebody at the PC: they go to the session but count as no typing here.
   */
  const TERMINAL_REPORT = /^(?:\x1b\[[IO]|\x1b\[\d+;\d+R|\x1b\[[?>=]?[\d;]*c|\x1b\[\??[\d;]*\$y|\x1b\[[\d;]*t|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1bP[^\x1b]*\x1b\\)+$/;

  /**
   * Sessions without hooks only (a command other than Claude Code): a running session with no output for this long is considered idle.
   * Claude Code sessions say what they do through their hooks instead (see handlers.hook): output pauses while
   * it still works, which made that guess call sessions "waiting" that weren't.
   */
  const QUIET_MS = 1500;
  /** Only sessions that worked at least this long raise "waiting for you" when they go quiet. */
  const MIN_WORK_MS = 3000;
  /** Start-up chatter should not count as finished work. */
  const STARTUP_GRACE_MS = 8000;
  /** How long after launch the "trust this folder?" prompt is watched for, and the pause between the keys answering it. */
  const TRUST_WATCH_MS = 45000;
  const TRUST_KEY_SPACING_MS = 300;
  const FONT_SIZES = { min: 8, max: 28, fallback: 13 };
  /** Where projects go when no group is chosen (the host uses the same name). */
  const DEFAULT_GROUP = 'Misc';
  const GROUP_NAME_MAX = 40;
  const ENTRY_NAME_MAX = 60;
  /** Text colours a group can give its terminals: light enough to read on the terminal background. */
  const GROUP_COLORS = [
    { name: 'Coral', value: '#ff8f88' }, { name: 'Orange', value: '#ffab70' }, { name: 'Amber', value: '#f0cf6e' },
    { name: 'Lime', value: '#b6e075' }, { name: 'Green', value: '#7fdc9f' }, { name: 'Teal', value: '#5fd9c8' },
    { name: 'Sky', value: '#7fcfff' }, { name: 'Blue', value: '#8fb0ff' }, { name: 'Violet', value: '#b9a0ff' },
    { name: 'Pink', value: '#f09be6' }, { name: 'Rose', value: '#ff9dbb' }, { name: 'Sand', value: '#d8c3a5' },
  ];
  /** The terminal colours and a group colour's tint (termcolor.js). */
  const { THEME, themeFor, tintOutput } = window.MaidTerm;

  const ICONS = {
    play: '<svg viewBox="0 0 16 16"><path class="fill" d="M4.5 2.9v10.2c0 .6.65.96 1.15.64l7.9-5.1a.75.75 0 0 0 0-1.28l-7.9-5.1a.75.75 0 0 0-1.15.64z"/></svg>',
    resume: '<svg viewBox="0 0 16 16"><path d="M2.5 8a5.5 5.5 0 1 0 1.8-4.07"/><path d="M2.4 2.2v2.6H5"/><path d="M8 5.2V8l1.9 1.2"/></svg>',
    stop: '<svg viewBox="0 0 16 16"><rect class="fill" x="4" y="4" width="8" height="8" rx="1.5"/></svg>',
    maximize: '<svg viewBox="0 0 16 16"><path d="M9.5 2.5h4v4M13.5 2.5 9 7M6.5 13.5h-4v-4M2.5 13.5 7 9"/></svg>',
    restore: '<svg viewBox="0 0 16 16"><path d="M13.5 2.5 9 7m0-3.5V7h3.5M2.5 13.5 7 9m0 3.5V9H3.5"/></svg>',
    close: '<svg viewBox="0 0 16 16"><path d="m4 4 8 8M12 4l-8 8"/></svg>',
    chevron: '<svg viewBox="0 0 16 16"><path d="m6 3.5 4.5 4.5L6 12.5" stroke-width="1.8"/></svg>',
    group: '<svg viewBox="0 0 16 16"><path d="M2.5 2.5h5.3l5.7 5.7-5.3 5.3-5.7-5.7z"/><circle class="fill" cx="5.6" cy="5.6" r="1.1"/></svg>',
    rename: '<svg viewBox="0 0 16 16"><path d="m11.1 2.6 2.3 2.3-8.1 8.1-3 .7.7-3z"/><path d="m9.6 4.1 2.3 2.3"/></svg>',
    plus: '<svg viewBox="0 0 16 16"><path d="M8 3.5v9M3.5 8h9"/></svg>',
    check: '<svg viewBox="0 0 16 16"><path d="m3.5 8.5 3 3 6-7" stroke-width="1.8"/></svg>',
    more: '<svg viewBox="0 0 16 16"><circle class="fill" cx="3.5" cy="8" r="1.4"/><circle class="fill" cx="8" cy="8" r="1.4"/><circle class="fill" cx="12.5" cy="8" r="1.4"/></svg>',
    up: '<svg viewBox="0 0 16 16"><path d="M8 13V3M3.5 7.5 8 3l4.5 4.5"/></svg>',
    down: '<svg viewBox="0 0 16 16"><path d="M8 3v10M3.5 8.5 8 13l4.5-4.5"/></svg>',
    color: '<svg viewBox="0 0 16 16"><path d="M8 2.2S3.6 6.7 3.6 9.6a4.4 4.4 0 0 0 8.8 0C12.4 6.7 8 2.2 8 2.2z"/></svg>',
    move: '<svg viewBox="0 0 16 16"><path d="M7.5 12.75H3.25c-.83 0-1.5-.67-1.5-1.5v-7c0-.83.67-1.5 1.5-1.5h2.6l1.5 1.5h5.4c.83 0 1.5.67 1.5 1.5v1.5"/><path d="M9 11h5.5M12.4 8.9l2.1 2.1-2.1 2.1"/></svg>',
    renameFolder: '<svg viewBox="0 0 16 16"><path d="M8.5 12.75H3.25c-.83 0-1.5-.67-1.5-1.5v-7c0-.83.67-1.5 1.5-1.5h2.6l1.5 1.5h5.4c.83 0 1.5.67 1.5 1.5v.5"/><path d="M12.25 7.75v6.5M10.75 7.75h3M10.75 14.25h3"/></svg>',
  };

  // ---- state ----------------------------------------------------------------------------------

  const state = {
    entries: [],              // saved entries { id, path, name, group }
    groups: [],               // { name, parent, collapsed, color }; among those of the same parent ('' = top level) in the order the list shows them
    sharedInfo: '',           // the text kept in every project's CLAUDE.md
    hub: null,                // the "all projects" folder, as a pseudo entry { id, name, path, group: '' } (not in the list)
    relocating: new Map(),    // entry id -> { path, files } while its folder is being moved
    rows: new Map(),          // entry id -> sidebar row elements
    groupEls: new Map(),      // group key -> sidebar group elements
    sessions: new Map(),      // session id -> open session (tile + terminal); a project can have several
    editor: null,             // the inline text editor that is open (group rename / new group), if any
    editing: false,           // the list's editing controls are shown; off at every start, never saved
    activeId: null,
    maximizedId: null,
    settings: { fontSize: FONT_SIZES.fallback, columns: 0, sidebarCollapsed: false, sidebarWidth: SIDEBAR_WIDTH.usual, command: '' },
    osBuild: 0,
  };

  const $ = (id) => document.getElementById(id);
  const els = {
    form: $('new-session'),
    pathField: document.querySelector('.path-field'),
    pathInput: $('path-input'),
    groupInput: $('group-input'),
    groupOptions: $('group-options'),
    formError: $('form-error'),
    columns: $('columns'),
    editToggle: $('edit-toggle'),
    sidebar: $('sidebar'),
    entryList: $('entry-list'),
    sideEmpty: $('side-empty'),
    sideSummary: $('side-summary'),
    groupMenu: $('group-menu'),
    confirm: $('confirm-dialog'),
    confirmTitle: $('confirm-title'),
    confirmDetail: $('confirm-detail'),
    confirmNote: $('confirm-note'),
    confirmOk: $('confirm-ok'),
    confirmCancel: $('confirm-cancel'),
    infoDialog: $('info-dialog'),
    infoText: $('info-text'),
    infoStatus: $('info-status'),
    infoSave: $('info-save'),
    hubButton: $('hub-btn'),
    hubCount: $('hub-count'),
    moveDialog: $('move-dialog'),
    moveTitle: $('move-title'),
    moveFrom: $('move-from'),
    movePath: $('move-path'),
    moveStatus: $('move-status'),
    moveOk: $('move-ok'),
    moveCancel: $('move-cancel'),
    renameDialog: $('rename-dialog'),
    renameCrumbs: $('rename-crumbs'),
    renameParent: $('rename-parent'),
    renameName: $('rename-name'),
    renameProjectOption: $('rename-project-option'),
    renameProject: $('rename-project'),
    renameProjectLabel: $('rename-project-label'),
    renameAffected: $('rename-affected'),
    renameStatus: $('rename-status'),
    renameOk: $('rename-ok'),
    stage: $('stage'),
    grid: $('grid'),
    emptyHint: $('empty-hint'),
    helpDialog: $('help-dialog'),
    helpScroll: $('help-scroll'),
    helpClaude: $('help-claude'),
    emptyCommand: $('empty-command'),
  };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function icon(svg, className) {
    const node = element('span', className);
    node.innerHTML = svg;
    return node;
  }

  function actionButton(action, svg, title) {
    const button = element('button');
    button.type = 'button';
    button.dataset.act = action;
    button.title = title;
    button.innerHTML = svg;
    return button;
  }

  /** First click arms the button, a second click within a moment runs the action. */
  function confirmThen(button, action) {
    if (button.classList.contains('confirming')) {
      disarm(button);
      action();
      return;
    }
    button.classList.add('confirming');
    button.dataset.icon = button.innerHTML;
    button.textContent = 'Sure?';
    button.confirmTimer = setTimeout(() => disarm(button), 2500);
  }

  function disarm(button) {
    clearTimeout(button.confirmTimer);
    if (!button.classList.contains('confirming')) return;
    button.classList.remove('confirming');
    button.innerHTML = button.dataset.icon;
  }

  /** A listed project, or the hub (the folder of the sessions about all projects). */
  const findEntry = (id) => (state.hub && id === state.hub.id ? state.hub : state.entries.find((entry) => entry.id === id));
  const isHub = (entryId) => !!state.hub && entryId === state.hub.id;
  const isLive = (session) => session.status === 'running' || session.status === 'starting';
  /** The open sessions (tiles) of a project, by their number (#1, #2, …). */
  const sessionsOf = (entryId) => [...state.sessions.values()]
    .filter((session) => session.entry.id === entryId)
    .sort((a, b) => a.number - b.number);
  const newSessionId = () => `s${Date.now().toString(36)}${Math.random().toString(36).slice(2, 7)}`;

  // Group names are unique regardless of case (the host enforces it); compare them the same way.
  const groupKey = (name) => (name || '').trim().toLowerCase();
  const sameGroup = (a, b) => groupKey(a) === groupKey(b);
  const findGroup = (name) => state.groups.find((group) => sameGroup(group.name, name));
  const entriesOf = (name) => state.entries.filter((entry) => sameGroup(entry.group, name));
  /** The groups nested directly in `parent` ('' for the top level), in their order. */
  const childrenOf = (parent) => state.groups.filter((group) => sameGroup(group.parent, parent));
  /** A group and the groups it is nested in, innermost first. */
  function lineageOf(name) {
    const chain = [];
    for (let group = findGroup(name); group && !chain.includes(group); group = findGroup(group.parent)) chain.push(group);
    return chain;
  }
  /** Whether group `name` is `ancestor` or nested in it, at any depth. */
  const isWithin = (name, ancestor) => lineageOf(name).some((group) => sameGroup(group.name, ancestor));
  /** The projects of a group and of the groups nested in it. */
  const entriesWithin = (name) => state.entries.filter((entry) => isWithin(entry.group, name));
  const groupPath = (name) => lineageOf(name).reverse().map((group) => group.name).join(' / ');
  /** The groups as the list shows them: each one followed by the groups nested in it, with its depth (0 = top level). */
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
  /** A modal dialog has the keyboard; the dashboard's own shortcuts and focus handling stay out of its way. */
  const modalOpen = () => els.confirm.open || els.infoDialog.open || els.moveDialog.open || els.renameDialog.open
    || els.helpDialog.open;


  // ---- group colour ---------------------------------------------------------------------------

  /** The colour ("#rrggbb") of a group: its own, else that of the nearest group it is nested in that has one; '' for none. */
  const groupColorOf = (name) => { const group = lineageOf(name).find((candidate) => candidate.color); return (group && group.color) || ''; };
  /** The colour a project's terminals write in: its group's. */
  const colorOf = (entry) => groupColorOf(entry.group);

  /** ConPTY only repaints on an actual size change, so the size is nudged to get the screen painted again. */
  function repaint(session) {
    post({ type: 'resize', id: session.id, cols: session.term.cols - 1, rows: session.term.rows });
    post({ type: 'resize', id: session.id, cols: session.term.cols, rows: session.term.rows });
  }

  /** Brings a session's terminal in line with the colour of its project's group. */
  function applyGroupColor(session) {
    const color = colorOf(session.entry);
    if (color === session.color) return;
    session.color = color;
    session.term.options.theme = themeFor(color);
    // What is on the screen in shades of the previous colour keeps them until it is painted again.
    if (session.status === 'running') repaint(session);
  }

  /** Entries in the order the list shows them: group by group, nested ones included (the hub, which is not listed, first). */
  function orderedEntries() {
    const ordered = state.hub ? [state.hub] : [];
    const seen = new Set();
    for (const { group } of groupTree()) {
      for (const entry of entriesOf(group.name)) {
        ordered.push(entry);
        seen.add(entry.id);
      }
    }
    for (const entry of state.entries) if (!seen.has(entry.id)) ordered.push(entry);
    return ordered;
  }

  // ---- sidebar --------------------------------------------------------------------------------

  /** Rebuilds the list from the groups and entries; the sessions (tiles) are untouched. */
  function renderSidebar() {
    cancelDrag();
    if (state.editor) state.editor.finish(false);
    closeMenu();
    els.entryList.textContent = '';
    state.rows.clear();
    state.groupEls.clear();

    // Parents come before the groups nested in them, so their sections are there to take them.
    for (const { group, depth } of groupTree()) {
      const parent = depth > 0 && state.groupEls.get(groupKey(group.parent));
      (parent ? parent.children : els.entryList).append(buildGroup(group));
    }
    // Entries whose group is unknown (should not happen) still get a row rather than vanishing.
    for (const entry of state.entries) {
      if (!state.rows.has(entry.id)) els.entryList.append(buildRow(entry));
    }

    for (const entry of state.entries) refreshEntry(entry.id);
    for (const group of state.groups) refreshGroup(group.name);
    updateGroupOptions();
    updateSummary();
  }

  /** A group's section of the list: its header, its projects, then the groups nested in it (filled by the caller). */
  function buildGroup(group) {
    const section = element('li', 'group');
    section.dataset.group = group.name;
    section.classList.toggle('is-collapsed', !!group.collapsed);
    section.classList.toggle('is-default', sameGroup(group.name, DEFAULT_GROUP));
    const head = element('div', 'group-head');
    const name = element('span', 'group-name', group.name);
    const color = groupColorOf(group.name);
    if (color) name.style.color = color;
    const count = element('span', 'group-count');
    const actions = element('div', 'group-actions');
    const siblings = childrenOf(group.parent);
    const index = siblings.indexOf(group);
    const up = actionButton('groupUp', ICONS.up, 'Move this group up');
    const down = actionButton('groupDown', ICONS.down, 'Move this group down');
    up.disabled = index === 0;
    down.disabled = index === siblings.length - 1;
    const parent = findGroup(group.parent);
    const nested = childrenOf(group.name).length > 0;
    actions.append(
      up, down,
      actionButton('color', ICONS.color, 'Text colour of the terminals in this group'),
      actionButton('groupMenu', ICONS.more, 'Rename, add a group inside, put inside another group…'),
      actionButton('removeGroup', ICONS.close,
        `Remove this group (its projects move to ${parent ? parent.name : DEFAULT_GROUP}${nested ? ', the groups in it up a level' : ''})`));
    head.append(icon(ICONS.chevron, 'group-chevron'), name, count, element('span', 'dot'), actions);
    const list = element('ul', 'group-entries');
    const empty = element('div', 'group-empty', 'No projects yet');
    const children = element('ul', 'group-children');
    section.append(head, list, empty, children);

    head.addEventListener('click', (event) => {
      // The click that ends a drag of the group is not a click on its header.
      if (drag.suppressClick || event.target.closest('input')) return;
      const button = event.target.closest('button[data-act]');
      if (!button) {
        setGroupCollapsed(group.name, !group.collapsed);
        return;
      }
      if (!state.editing) return;
      switch (button.dataset.act) {
        case 'groupUp': post({ type: 'reorderGroup', name: group.name, parent: group.parent || '', index: index - 1 }); break;
        case 'groupDown': post({ type: 'reorderGroup', name: group.name, parent: group.parent || '', index: index + 1 }); break;
        case 'color': openColorMenu(group, button); break;
        case 'groupMenu': openGroupMenu(group, button); break;
        case 'removeGroup':
          if (entriesOf(group.name).length || nested) confirmThen(button, () => post({ type: 'removeGroup', name: group.name }));
          else post({ type: 'removeGroup', name: group.name });
          break;
      }
    });

    for (const entry of entriesOf(group.name)) list.append(buildRow(entry));
    state.groupEls.set(groupKey(group.name), { section, head, name, count, empty, children });
    return section;
  }

  function buildRow(entry) {
    const row = element('li', 'entry');
    row.title = entry.path;
    row.dataset.id = entry.id;
    const dot = element('span', 'dot');
    const text = element('div', 'entry-text');
    const name = element('div', 'entry-name', entry.name);
    const detail = element('div', 'entry-detail', '‎' + entry.path);
    text.append(name, detail);
    const count = element('span', 'entry-count');
    count.title = 'Sessions running in this project';
    count.hidden = true;
    const actions = element('div', 'entry-actions');
    const launchButton = actionButton('launch', ICONS.play, 'Launch');
    actions.append(
      launchButton,
      actionButton('resume', ICONS.resume, 'Launch and resume the last conversation (--continue)'),
      actionButton('stop', ICONS.stop, 'Stop the sessions of this project'),
      actionButton('menu', ICONS.more, 'Rename, reorder, move to a group, remove…'),
      actionButton('remove', ICONS.close, 'Remove from the list'));
    row.append(dot, text, count, actions);

    row.addEventListener('click', (event) => {
      // The click that ends a drag is not a click on the row; neither is one in the name editor.
      if (drag.suppressClick || event.target.closest('input')) return;
      const button = event.target.closest('button[data-act]');
      if (!button) {
        activateEntry(entry.id);
        return;
      }
      switch (button.dataset.act) {
        case 'launch': launchEntry(entry.id); break;
        case 'resume': launchEntry(entry.id, { resume: true }); break;
        case 'stop': confirmThen(button, () => { for (const session of sessionsOf(entry.id)) stop(session.id); }); break;
        case 'menu': if (state.editing) openEntryMenu(entry.id, button); break;
        case 'remove': if (state.editing) removeEntry(entry.id); break;
      }
    });
    // Double-clicking a project that already runs goes to it rather than starting another session.
    row.addEventListener('dblclick', (event) => {
      if (event.target.closest('button, input')) return;
      if (sessionsOf(entry.id).some(isLive)) activateEntry(entry.id);
      else launchEntry(entry.id);
    });

    state.rows.set(entry.id, { row, name, detail, count, launchButton });
    return row;
  }

  /**
   * The group header mirrors the sessions inside it, those of the groups nested in it included, so a
   * collapsed group still shows what is going on.
   */
  function refreshGroup(name) {
    const view = state.groupEls.get(groupKey(name));
    if (!view) return;
    const entries = entriesWithin(name);
    const own = entriesOf(name).length;
    let running = 0;
    let starting = false;
    let busy = false;
    let attention = false;
    for (const entry of entries) {
      for (const session of sessionsOf(entry.id)) {
        if (session.status === 'starting') starting = true;
        if (session.status !== 'running') continue;
        running++;
        if (session.attention) attention = true;
        else if (session.busy) busy = true;
      }
    }
    const flags = {
      'has-entries': own > 0, // projects of its own (the default group cannot be removed then)
      'is-starting': starting && !running,
      'is-running': running > 0,
      'is-busy': busy && !attention,
      'is-attention': attention,
    };
    for (const [flag, on] of Object.entries(flags)) view.head.classList.toggle(flag, on);
    view.count.textContent = entries.length ? String(entries.length) : '';
    view.empty.hidden = own > 0 || childrenOf(name).length > 0;
  }

  function setGroupCollapsed(name, collapsed, { save = true } = {}) {
    const group = findGroup(name);
    if (!group || !!group.collapsed === collapsed) return;
    group.collapsed = collapsed;
    const view = state.groupEls.get(groupKey(name));
    if (view) view.section.classList.toggle('is-collapsed', collapsed);
    if (save) post({ type: 'toggleGroup', name: group.name, collapsed });
  }

  /** Expands a group and the groups it is nested in, so what is in it can be seen. */
  function revealGroup(name) {
    for (const group of lineageOf(name)) setGroupCollapsed(group.name, false);
  }

  function updateGroupOptions() {
    els.groupOptions.textContent = '';
    for (const { group, depth } of groupTree()) {
      const option = document.createElement('option');
      option.value = group.name;
      if (depth > 0) option.label = groupPath(group.name);
      els.groupOptions.append(option);
    }
  }

  /**
   * A one-line text editor for a group name: Enter or moving away commits (when something changed),
   * Escape cancels. Only one is open at a time.
   */
  function inlineInput({ value = '', placeholder = '', maxLength = GROUP_NAME_MAX, allowEmpty = false, onCommit, onCancel }) {
    if (state.editor) state.editor.finish(true);
    const input = element('input', 'inline-input');
    input.type = 'text';
    input.value = value;
    input.placeholder = placeholder;
    input.maxLength = maxLength;
    input.spellcheck = false;
    let done = false;
    input.finish = (commit) => {
      if (done) return;
      done = true;
      if (state.editor === input) state.editor = null;
      const text = input.value.trim();
      if (commit && (text || allowEmpty) && text !== value) onCommit(text);
      else onCancel();
      setTimeout(refocusTerminal, 0);
    };
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') input.finish(true);
      else if (event.key === 'Escape') input.finish(false);
      else return;
      event.preventDefault();
    });
    input.addEventListener('blur', () => input.finish(true));
    state.editor = input;
    requestAnimationFrame(() => { input.focus(); input.select(); });
    return input;
  }

  function startRenameGroup(group, nameEl) {
    const input = inlineInput({
      value: group.name,
      placeholder: 'Group name',
      onCommit: (name) => {
        nameEl.textContent = name;
        input.replaceWith(nameEl);
        post({ type: 'renameGroup', name: group.name, newName: name });
      },
      onCancel: () => input.replaceWith(nameEl),
    });
    nameEl.replaceWith(input);
  }

  const folderName = (path) => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path;

  /** Edits a project's display name in place; clearing it goes back to the folder's name. */
  function startRenameEntry(id) {
    const entry = findEntry(id);
    const row = state.rows.get(id);
    if (!entry || !row) return;
    const input = inlineInput({
      value: entry.name,
      placeholder: folderName(entry.path),
      maxLength: ENTRY_NAME_MAX,
      allowEmpty: true,
      onCommit: (name) => {
        row.name.textContent = name || folderName(entry.path);
        input.replaceWith(row.name);
        post({ type: 'renameEntry', id, name });
      },
      onCancel: () => input.replaceWith(row.name),
    });
    row.name.replaceWith(input);
    row.scrollIntoView({ block: 'nearest' });
  }

  /** Opens an editor for the name of a new group: last at the top level, or last among the groups nested in `parent`. */
  function startNewGroup(parent = '') {
    if (!state.editing) return;
    const holder = parent && state.groupEls.get(groupKey(parent));
    if (parent && !holder) return;
    if (state.settings.sidebarCollapsed) toggleSidebar(false);
    if (parent) revealGroup(parent);
    const section = element('li', 'group is-editing');
    const head = element('div', 'group-head');
    const input = inlineInput({
      placeholder: parent ? `New group in ${parent}` : 'New group name',
      onCommit: (name) => { section.remove(); post({ type: 'addGroup', name, parent }); },
      onCancel: () => section.remove(),
    });
    head.append(icon(ICONS.chevron, 'group-chevron'), input);
    section.append(head);
    (holder ? holder.children : els.entryList).append(section);
    section.scrollIntoView({ block: 'nearest' });
  }

  /** Puts keyboard focus back on the active terminal after a menu or editor, unless another input took it. */
  function refocusTerminal() {
    if (modalOpen()) return;
    const active = document.activeElement;
    if (active && active !== document.body && active.isConnected && active.matches('input, textarea')) return;
    const session = state.sessions.get(state.activeId);
    if (session && session.el.termHost.offsetParent) session.term.focus();
  }

  // ---- popup menu: a project's (rename, order, group, remove) or a group's colours ------------

  const menu = { closedAt: 0, closedFor: null };

  /** Empties the popup menu for `owner` to fill; null when the click only closed the menu `owner` had open. */
  function beginMenu(owner) {
    // The click that reopens the menu was preceded by the mousedown that closed it: that is a toggle.
    if (menu.closedFor === owner && performance.now() - menu.closedAt < 400) return null;
    closeMenu();
    els.groupMenu.dataset.owner = owner;
    return els.groupMenu;
  }

  function showMenu(anchor) {
    const view = els.groupMenu;
    view.hidden = false;
    // The button only shows while its row is hovered; fall back to the row if it has no box (keyboard, scripts).
    const rect = (anchor.offsetParent ? anchor : anchor.closest('.entry, .group-head') || anchor).getBoundingClientRect();
    const { offsetWidth: width, offsetHeight: height } = view;
    const below = rect.bottom + 4 + height <= window.innerHeight;
    view.style.left = `${Math.max(4, Math.min(rect.left, window.innerWidth - width - 4))}px`;
    view.style.top = `${below ? rect.bottom + 4 : Math.max(4, rect.top - 4 - height)}px`;
  }

  function menuItem(svg, label, { className = '', disabled = false } = {}) {
    const item = element('button', `menu-item ${className}`.trim());
    item.type = 'button';
    item.innerHTML = svg;
    item.append(element('span', '', label));
    item.disabled = disabled;
    return item;
  }

  function openEntryMenu(entryId, anchor) {
    const entry = findEntry(entryId);
    const view = entry && beginMenu(entryId);
    if (!view) return;
    const siblings = entriesOf(entry.group);
    const position = siblings.indexOf(entry);
    const reorder = (index) => { post({ type: 'reorder', id: entryId, group: entry.group, index }); closeMenu(); };

    view.append(element('div', 'menu-title', entry.name));
    const rename = menuItem(ICONS.rename, 'Rename');
    rename.addEventListener('click', () => { closeMenu(); startRenameEntry(entryId); });
    const relocate = menuItem(ICONS.move, 'Move folder…', { disabled: state.relocating.has(entryId) });
    relocate.addEventListener('click', () => { closeMenu(); openMoveDialog(entryId); });
    const renameFolder = menuItem(ICONS.renameFolder, 'Rename folder…', { disabled: state.relocating.has(entryId) });
    renameFolder.addEventListener('click', () => { closeMenu(); openRenameDialog(entryId); });
    const up = menuItem(ICONS.up, 'Move up', { disabled: position <= 0 });
    up.addEventListener('click', () => reorder(position - 1));
    const down = menuItem(ICONS.down, 'Move down', { disabled: position < 0 || position >= siblings.length - 1 });
    down.addEventListener('click', () => reorder(position + 1));
    view.append(rename, relocate, renameFolder, up, down, element('div', 'menu-sep'), element('div', 'menu-title', 'Move to group'));

    for (const { group, depth } of groupTree()) {
      const current = sameGroup(group.name, entry.group);
      const item = menuItem(ICONS.check, group.name, { className: current ? 'choice current' : 'choice' });
      item.style.setProperty('--depth', depth);
      item.addEventListener('click', () => {
        if (!current) post({ type: 'moveEntry', id: entryId, group: group.name });
        closeMenu();
      });
      view.append(item);
    }
    const create = menuItem(ICONS.plus, 'New group…', { className: 'create' });
    create.addEventListener('click', () => {
      const holder = element('div', 'menu-input');
      holder.append(inlineInput({
        placeholder: 'New group name',
        onCommit: (name) => { post({ type: 'moveEntry', id: entryId, group: name }); closeMenu(); },
        onCancel: () => closeMenu(),
      }));
      create.replaceWith(holder);
    });
    const remove = menuItem(ICONS.close, 'Remove from the list…', { className: 'danger' });
    remove.addEventListener('click', () => { closeMenu(); removeEntry(entryId); });
    view.append(create, element('div', 'menu-sep'), remove);
    showMenu(anchor);
  }

  /**
   * A group's menu: rename it, add a group inside it, or put it inside another group (or back at the
   * top level) with everything in it.
   */
  function openGroupMenu(group, anchor) {
    const view = beginMenu(`group:${groupKey(group.name)}`);
    if (!view) return;
    view.append(element('div', 'menu-title', group.name));
    const rename = menuItem(ICONS.rename, 'Rename');
    rename.addEventListener('click', () => {
      closeMenu();
      const own = state.groupEls.get(groupKey(group.name));
      if (own) startRenameGroup(group, own.name);
    });
    const add = menuItem(ICONS.plus, 'New group inside…');
    add.addEventListener('click', () => { closeMenu(); startNewGroup(group.name); });
    view.append(rename, add, element('div', 'menu-sep'), element('div', 'menu-title', 'Put inside'));

    // Last in the group chosen, which opens to show it there.
    const place = (parent) => {
      if (!sameGroup(parent, group.parent)) {
        if (parent) revealGroup(parent);
        post({ type: 'reorderGroup', name: group.name, parent });
      }
      closeMenu();
    };
    const top = menuItem(ICONS.check, 'Top level', { className: group.parent ? 'choice' : 'choice current' });
    top.addEventListener('click', () => place(''));
    view.append(top);
    for (const { group: other, depth } of groupTree()) {
      // Not into itself, nor into a group nested in it.
      if (isWithin(other.name, group.name)) continue;
      const current = sameGroup(other.name, group.parent);
      const item = menuItem(ICONS.check, other.name, { className: current ? 'choice current' : 'choice' });
      item.style.setProperty('--depth', depth + 1);
      item.addEventListener('click', () => place(other.name));
      view.append(item);
    }
    showMenu(anchor);
  }

  /**
   * The colour the terminals of a group's projects write in: one of the palette, any other, or none —
   * which, for a group nested in one with a colour, is that group's colour.
   */
  function openColorMenu(group, anchor) {
    const view = beginMenu(`colour:${groupKey(group.name)}`);
    if (!view) return;
    const choose = (color) => { post({ type: 'setGroupColor', name: group.name, color }); closeMenu(); };
    const from = lineageOf(group.parent).find((candidate) => candidate.color);

    view.append(element('div', 'menu-title', `Terminal text — ${group.name}`));
    const swatches = element('div', 'menu-swatches');
    for (const color of GROUP_COLORS) {
      const swatch = element('button', color.value === group.color ? 'swatch current' : 'swatch');
      swatch.type = 'button';
      swatch.title = color.name;
      swatch.style.background = color.value;
      swatch.addEventListener('click', () => choose(color.value));
      swatches.append(swatch);
    }
    const custom = element('label', 'menu-item');
    const picker = element('input');
    picker.type = 'color';
    picker.value = group.color || (from && from.color) || THEME.foreground;
    picker.addEventListener('change', () => choose(picker.value));
    custom.append(picker, element('span', '', 'Another colour…'));
    const none = menuItem(ICONS.check, from ? `Same as ${from.name}` : 'No colour', { className: group.color ? 'choice' : 'choice current' });
    none.addEventListener('click', () => choose(''));
    view.append(swatches, custom, element('div', 'menu-sep'), none);
    showMenu(anchor);
  }

  function closeMenu() {
    const view = els.groupMenu;
    if (view.hidden) return;
    view.hidden = true;
    menu.closedAt = performance.now();
    menu.closedFor = view.dataset.owner || null;
    if (state.editor && view.contains(state.editor)) state.editor.finish(false);
    view.textContent = '';
    setTimeout(refocusTerminal, 0);
  }

  // ---- reordering by drag ---------------------------------------------------------------------

  /**
   * A row picked up with the mouse travels through the list as the pointer moves (a ghost of it
   * follows the pointer); where it is dropped is sent to the host. A whole group, taken by its header,
   * stays where it is while a line (or, to put it inside another group, that group's highlighted
   * header) shows where it would go: groups nested in groups are of all heights, and moving one
   * under the pointer would make it jump back and forth. While a group is carried the list shows only
   * headers, so tall groups are no obstacle.
   */
  const drag = {
    kind: null, id: null, node: null, grip: null, active: false,
    startX: 0, startY: 0, offsetX: 0, offsetY: 0, ghost: null, from: null, suppressClick: false,
    target: null, line: null, // a group's drop target ({ kind: 'before' | 'after' | 'into' | 'end', name }) and its marker
  };

  els.entryList.addEventListener('mousedown', (event) => {
    if (!state.editing || event.button !== 0 || event.target.closest('button, input')) return;
    const row = event.target.closest('.entry');
    const head = row ? null : event.target.closest('.group-head');
    const section = head && head.closest('.group');
    if (row && row.dataset.id) Object.assign(drag, { kind: 'entry', id: row.dataset.id, node: row, grip: row });
    else if (section && section.dataset.group) Object.assign(drag, { kind: 'group', id: section.dataset.group, node: section, grip: head });
    else return;
    Object.assign(drag, { active: false, startX: event.clientX, startY: event.clientY });
  });

  window.addEventListener('mousemove', (event) => {
    if (!drag.node) return;
    if (!drag.active) {
      if (Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < 5) return;
      startDrag(event);
    }
    moveDrag(event);
  });

  window.addEventListener('mouseup', () => {
    if (drag.active) endDrag(true);
    drag.node = null;
    drag.id = null;
  });

  function startDrag(event) {
    if (state.editor) state.editor.finish(true);
    closeMenu();
    const rect = drag.grip.getBoundingClientRect();
    const ghost = drag.grip.cloneNode(true);
    ghost.classList.add('drag-ghost');
    ghost.style.width = `${rect.width}px`;
    document.body.append(ghost);
    Object.assign(drag, {
      active: true,
      ghost,
      from: { parent: drag.node.parentNode, next: drag.node.nextSibling },
      offsetX: event.clientX - rect.left,
      offsetY: event.clientY - rect.top,
    });
    drag.node.classList.add('dragging');
    document.body.classList.add('is-dragging');
    document.body.classList.toggle('is-dragging-group', drag.kind === 'group');
    if (drag.kind === 'group') {
      drag.line = element('div', 'drop-line');
      drag.line.hidden = true;
      document.body.append(drag.line);
    }
  }

  function moveDrag(event) {
    drag.ghost.style.left = `${event.clientX - drag.offsetX}px`;
    drag.ghost.style.top = `${event.clientY - drag.offsetY}px`;

    // Near the top or bottom edge the list scrolls along.
    const edge = els.entryList.getBoundingClientRect();
    if (event.clientY < edge.top + 28) els.entryList.scrollTop -= 8;
    else if (event.clientY > edge.bottom - 28) els.entryList.scrollTop += 8;

    // The ghost lets the pointer through, so this is what is underneath it.
    const under = document.elementFromPoint(event.clientX, event.clientY);
    if (!under || !els.sidebar.contains(under)) return;
    const upperHalf = (target) => { const rect = target.getBoundingClientRect(); return event.clientY < rect.top + rect.height / 2; };
    const sections = [...els.entryList.querySelectorAll(':scope > .group:not(.dragging):not(.is-editing)')];
    const last = sections[sections.length - 1];
    // Below the last group means "last": for a group the last position, for a project the end of the
    // last group. The list shrinks while a group is carried, so that can be the sidebar's bare bottom.
    const belowAll = !!last && !under.closest('.group') && event.clientY > last.getBoundingClientRect().bottom;
    if (!els.entryList.contains(under) && !belowAll) return;

    if (drag.kind === 'group') {
      const target = belowAll ? { kind: 'end', name: '' } : groupDropTarget(event, under);
      if (target !== undefined) showGroupDropTarget(target, last);
      return;
    }
    const row = under.closest('.entry:not(.dragging)');
    if (row) {
      row.parentNode.insertBefore(drag.node, upperHalf(row) ? row : row.nextSibling);
      return;
    }
    // On a group's header or its "no projects" line: last in that group (a collapsed one included).
    const section = under.closest('.group') || (belowAll ? last : null);
    if (section && (belowAll || under.closest('.group-head, .group-empty'))) {
      const list = section.querySelector(':scope > .group-entries');
      if (list && drag.node !== list.lastElementChild) list.append(drag.node);
    }
  }

  /**
   * Where the carried group would go, by the header under the pointer: its upper third puts it before
   * that group, the middle inside it (last), the lower third after it — or, when that group shows
   * groups nested in it, first among those, which is what is seen right below. Null over the carried
   * group itself (nowhere to go); undefined between headers (the target stays as it was).
   */
  function groupDropTarget(event, under) {
    const head = under.closest('.group-head');
    const section = head && head.parentNode;
    if (!section || !section.dataset.group) return undefined;
    if (section.closest('.dragging')) return null;
    const name = section.dataset.group;
    const rect = head.getBoundingClientRect();
    const at = (event.clientY - rect.top) / rect.height;
    if (at < 1 / 3) return { kind: 'before', name };
    if (at <= 2 / 3) return { kind: 'into', name };
    const group = findGroup(name);
    const first = group && !group.collapsed && childrenOf(name).find((child) => !sameGroup(child.name, drag.id));
    return first ? { kind: 'before', name: first.name } : { kind: 'after', name };
  }

  function showGroupDropTarget(target, last) {
    drag.target = target;
    for (const head of els.entryList.querySelectorAll('.drop-into')) head.classList.remove('drop-into');
    drag.line.hidden = true;
    const view = target && state.groupEls.get(groupKey(target.name));
    if (target && target.kind === 'into') {
      if (view) view.head.classList.add('drop-into');
      return;
    }
    // The line spans the header of the level the group would join, below the group it would follow.
    const section = target && (target.kind === 'end' ? last : view && view.section);
    if (!section) return;
    const head = section.querySelector(':scope > .group-head').getBoundingClientRect();
    const y = target.kind === 'before' ? head.top - 2 : section.getBoundingClientRect().bottom + 1;
    Object.assign(drag.line.style, { left: `${head.left}px`, width: `${head.width}px`, top: `${y}px` });
    drag.line.hidden = false;
  }

  /** The message that puts the carried group where `target` says. */
  function groupDropMessage(target) {
    const name = drag.id;
    if (target.kind === 'end') return { type: 'reorderGroup', name, parent: '' };
    if (target.kind === 'into') return { type: 'reorderGroup', name, parent: target.name };
    const other = findGroup(target.name);
    const siblings = childrenOf(other.parent).filter((group) => !sameGroup(group.name, name));
    const index = siblings.indexOf(other) + (target.kind === 'after' ? 1 : 0);
    return { type: 'reorderGroup', name, parent: other.parent || '', index };
  }

  function endDrag(commit) {
    const node = drag.node;
    const target = drag.target;
    drag.ghost.remove();
    if (drag.line) drag.line.remove();
    for (const head of els.entryList.querySelectorAll('.drop-into')) head.classList.remove('drop-into');
    node.classList.remove('dragging');
    document.body.classList.remove('is-dragging', 'is-dragging-group');
    Object.assign(drag, { active: false, ghost: null, line: null, target: null, suppressClick: true });
    setTimeout(() => { drag.suppressClick = false; }, 0); // the click that follows the drop is not a click on what was dropped

    if (drag.kind === 'group') {
      if (!commit || !target || (target.kind !== 'end' && !findGroup(target.name))) return;
      // Put inside a folded group, it is opened so the group can be seen where it went.
      if (target.kind === 'into') revealGroup(target.name);
      post(groupDropMessage(target));
      return;
    }
    const moved = node.parentNode !== drag.from.parent || node.nextSibling !== drag.from.next;
    if (!moved) return;
    const putBack = () => drag.from.parent.insertBefore(node, drag.from.next);
    if (!commit) {
      putBack();
      return;
    }
    const section = node.closest('.group');
    if (section) post({ type: 'reorder', id: drag.id, group: section.dataset.group, index: [...node.parentNode.children].indexOf(node) });
    else putBack();
  }

  function cancelDrag() {
    if (!drag.active) return;
    endDrag(false);
    drag.node = null;
    drag.id = null;
  }

  // ---- confirmation dialog --------------------------------------------------------------------

  /** Asks a yes/no question in a modal dialog; resolves true when the action was confirmed. */
  function confirmDialog({ title, detail = '', note = '', action = 'OK' }) {
    const dialog = els.confirm;
    els.confirmTitle.textContent = title;
    els.confirmDetail.textContent = detail;
    els.confirmDetail.hidden = !detail;
    els.confirmNote.textContent = note;
    els.confirmNote.hidden = !note;
    els.confirmOk.textContent = action;
    return new Promise((resolve) => {
      dialog.addEventListener('close', () => {
        resolve(dialog.returnValue === 'ok');
        setTimeout(refocusTerminal, 0);
      }, { once: true });
      dialog.returnValue = '';
      closeMenu();
      dialog.showModal();
      els.confirmOk.focus();
    });
  }

  els.confirmOk.addEventListener('click', () => els.confirm.close('ok'));
  els.confirmCancel.addEventListener('click', () => els.confirm.close('cancel'));
  // A click on the backdrop lands on the dialog element itself.
  els.confirm.addEventListener('click', (event) => { if (event.target === els.confirm) els.confirm.close('cancel'); });

  /** Takes a project off the list, after asking; a running session in it is stopped along the way. */
  async function removeEntry(id) {
    const entry = findEntry(id);
    if (!entry) return;
    const live = sessionsOf(id).filter(isLive).length;
    const confirmed = await confirmDialog({
      title: `Remove ${entry.name} from the list?`,
      detail: entry.path,
      note: live > 1 ? `Its ${live} running sessions will be stopped first.`
        : live ? 'Its running session will be stopped first.'
        : state.sharedInfo ? 'The folder and its files stay where they are; only the shared info is taken out of its CLAUDE.md and AGENTS.md.'
        : 'The folder and its files stay where they are.',
      action: 'Remove',
    });
    if (confirmed && findEntry(id)) post({ type: 'remove', id });
  }

  // ---- shared info ----------------------------------------------------------------------------

  /** Escape closes the editor, but text that was not saved survives the first one. */
  let infoDiscardArmed = false;

  function setInfoStatus(message) {
    els.infoStatus.textContent = message;
    els.infoStatus.hidden = !message;
  }

  /** Edits the text Maid keeps in the CLAUDE.md of every project in the list. */
  function openInfoDialog() {
    if (!state.editing || modalOpen()) return;
    closeMenu();
    if (state.editor) state.editor.finish(true);
    const count = state.entries.length;
    els.infoText.value = state.sharedInfo;
    els.infoSave.textContent = count ? `Save to ${count} project${count === 1 ? '' : 's'}` : 'Save';
    els.infoSave.disabled = false;
    infoDiscardArmed = false;
    setInfoStatus('');
    els.infoDialog.showModal();
    els.infoText.focus();
  }

  function saveInfo() {
    if (els.infoSave.disabled) return;
    els.infoSave.disabled = true; // until the host answers with "sharedInfoSaved"
    post({ type: 'sharedInfo', text: els.infoText.value });
  }

  els.infoSave.addEventListener('click', saveInfo);
  $('info-cancel').addEventListener('click', () => els.infoDialog.close());
  els.infoDialog.addEventListener('cancel', (event) => {
    if (infoDiscardArmed || els.infoText.value === state.sharedInfo) return;
    event.preventDefault();
    infoDiscardArmed = true;
    setInfoStatus('Not saved yet — press Escape again to discard the changes.');
  });
  els.infoDialog.addEventListener('close', () => setTimeout(refocusTerminal, 0));
  els.infoText.addEventListener('input', () => {
    infoDiscardArmed = false;
    setInfoStatus('');
  });
  els.infoText.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || !event.ctrlKey) return;
    event.preventDefault();
    saveInfo();
  });

  // ---- moving a project's folder --------------------------------------------------------------

  /** The project the move dialog is about. */
  let moveFor = null;

  function setMoveStatus(message, { error = false } = {}) {
    els.moveStatus.textContent = message;
    els.moveStatus.hidden = !message;
    els.moveStatus.classList.toggle('error', error);
  }

  /** While the files move the dialog only reports; closing it leaves the move running. */
  function setMoveBusy(busy) {
    els.moveOk.disabled = busy;
    els.movePath.disabled = busy;
    $('move-browse').disabled = busy;
    els.moveCancel.textContent = busy ? 'Close' : 'Cancel';
  }

  /** Asks where a project's folder should go; the host moves the files and points the entry there. */
  function openMoveDialog(id) {
    const entry = findEntry(id);
    if (!entry || isHub(id) || !state.editing || modalOpen()) return;
    closeMenu();
    if (state.editor) state.editor.finish(true);
    moveFor = id;
    els.moveTitle.textContent = `Move ${entry.name} to another folder`;
    els.moveFrom.textContent = entry.path;
    els.movePath.value = entry.path;
    const moving = state.relocating.get(id);
    setMoveBusy(!!moving);
    setMoveStatus(moving ? `Moving to ${moving.path}…` : '');
    els.moveDialog.showModal();
    els.movePath.focus();
    // The folder's own name is what is most often changed.
    els.movePath.setSelectionRange(entry.path.length - folderName(entry.path).length, entry.path.length);
  }

  function submitMove() {
    const path = els.movePath.value.trim();
    if (els.moveOk.disabled || !moveFor) return;
    if (!path) {
      els.movePath.focus();
      return;
    }
    setMoveBusy(true);
    setMoveStatus('Moving…');
    post({ type: 'relocate', id: moveFor, path });
  }

  els.moveOk.addEventListener('click', submitMove);
  els.moveCancel.addEventListener('click', () => els.moveDialog.close());
  els.movePath.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    submitMove();
  });
  els.movePath.addEventListener('input', () => setMoveStatus(''));
  $('move-browse').addEventListener('click', () => {
    const value = els.movePath.value.trim().replace(/[\\/]+$/, '');
    post({ type: 'browse', purpose: 'move', path: value.slice(0, Math.max(0, value.length - folderName(value).length)) });
  });
  els.moveDialog.addEventListener('close', () => {
    moveFor = null;
    setTimeout(refocusTerminal, 0);
  });

  // ---- renaming a folder ----------------------------------------------------------------------

  /**
   * The rename dialog: `full` is the path shown as clickable folders (the project's), `path` the one of
   * them to rename, `option` the project the "rename the project too" box was last set up for.
   */
  const renaming = { full: '', path: '', option: null, busy: false, blocked: false };

  const trimPath = (path) => path.replace(/[\\/]+$/, '');
  const samePath = (a, b) => trimPath(a).toLowerCase() === trimPath(b).toLowerCase();
  /** Whether `path` is `folder` or somewhere below it (case does not matter on Windows). */
  const inFolder = (path, folder) => samePath(path, folder) || trimPath(path).toLowerCase().startsWith(`${trimPath(folder).toLowerCase()}\\`);
  const parentOf = (path) => path.slice(0, path.length - folderName(path).length);

  /** A path's folders from the top, each with its own path; the drive (or network share) is not one of them. */
  function pathFolders(path) {
    const root = (path.match(/^\\\\[^\\]+\\[^\\]+/) || path.match(/^[A-Za-z]:/) || [''])[0];
    let current = root;
    const folders = path.slice(root.length).split(/[\\/]+/).filter(Boolean)
      .map((name) => ({ name, path: (current = `${current}\\${name}`) }));
    return { root, folders };
  }

  /** Still called after its folder (as Maid named it when it was added), so it would follow the folder's new name. */
  function namedAfterFolder(entry) {
    const name = entry.name.toLowerCase();
    const folder = folderName(entry.path).toLowerCase();
    return name === folder || (name.startsWith(`${folder} (`) && /^ \(\d+\)$/.test(name.slice(folder.length)));
  }

  function setRenameStatus(message, { error = false } = {}) {
    els.renameStatus.textContent = message;
    els.renameStatus.hidden = !message;
    els.renameStatus.classList.toggle('error', error);
  }

  function setRenameBusy(busy) {
    renaming.busy = busy;
    els.renameOk.disabled = busy || renaming.blocked;
    els.renameName.disabled = busy;
    $('rename-browse').disabled = busy;
    for (const crumb of els.renameCrumbs.querySelectorAll('button')) crumb.disabled = busy;
  }

  /** Asks for a folder's new name: the project's own folder, one above it (a click in the path) or any other. */
  function openRenameDialog(id) {
    const entry = findEntry(id);
    if (!entry || isHub(id) || !state.editing || modalOpen()) return;
    closeMenu();
    if (state.editor) state.editor.finish(true);
    els.renameDialog.showModal();
    showRenameFolder(entry.path, entry.path);
  }

  /** Shows `full` as a row of folders with `chosen` (one of them) as the folder to rename. */
  function showRenameFolder(full, chosen) {
    renaming.full = trimPath(full);
    renaming.path = trimPath(chosen);
    renaming.option = null;
    const { root, folders } = pathFolders(renaming.full);
    els.renameCrumbs.replaceChildren(element('span', 'crumb-root', `${root}\\`));
    folders.forEach((folder, index) => {
      if (index) els.renameCrumbs.append(element('span', 'crumb-sep', '\\'));
      const crumb = element('button', 'crumb', folder.name);
      crumb.type = 'button';
      const current = samePath(folder.path, renaming.path);
      crumb.classList.toggle('chosen', current);
      crumb.setAttribute('aria-pressed', String(current));
      crumb.title = current ? `The folder to rename: ${folder.path}` : `Rename ${folder.path} instead`;
      crumb.addEventListener('click', () => showRenameFolder(renaming.full, folder.path));
      els.renameCrumbs.append(crumb);
    });
    // Cut from the left when long (right-to-left box); the marks keep the path's own order, its last "\" included.
    els.renameParent.textContent = `‎${parentOf(renaming.path)}‎`;
    els.renameName.value = folderName(renaming.path);
    setRenameBusy(false);
    setRenameStatus('');
    updateRenamePreview();
    els.renameName.focus();
    els.renameName.select();
  }

  /** Lists the projects that follow the rename and where they end up; offers to rename the project of that very folder. */
  function updateRenamePreview() {
    const folder = renaming.path;
    const name = els.renameName.value.trim();
    const target = name ? parentOf(folder) + name : '';
    const following = state.entries.filter((entry) => inFolder(entry.path, folder));

    const own = following.find((entry) => samePath(entry.path, folder));
    els.renameProjectOption.hidden = !own;
    if (own && renaming.option !== own.id) {
      renaming.option = own.id;
      els.renameProject.checked = namedAfterFolder(own);
    }
    if (own) els.renameProjectLabel.textContent = `Rename the project in the list too (${own.name} → ${name || '…'})`;

    // A running session keeps the folder in use (Windows refuses the rename): that is said here, not tried.
    const live = following.flatMap((entry) => sessionsOf(entry.id).filter(isLive));
    renaming.blocked = live.length > 0;
    els.renameOk.disabled = renaming.busy || renaming.blocked;

    els.renameAffected.replaceChildren();
    if (!following.length) {
      els.renameAffected.append(element('p', '', 'No project in the list is in this folder.'));
      return;
    }
    els.renameAffected.append(element('p', '', following.length === 1
      ? 'This project in the list follows it to the new path:'
      : `These ${following.length} projects in the list follow it to the new path:`));
    const list = element('ul', 'rename-list');
    const shown = 8;
    for (const entry of following.slice(0, shown)) {
      const item = element('li');
      item.title = entry.path;
      item.append(element('span', 'rename-list-name', entry.name),
        element('span', 'dialog-path', target ? target + trimPath(entry.path).slice(folder.length) : entry.path));
      list.append(item);
    }
    if (following.length > shown) list.append(element('li', 'rename-list-more', `and ${following.length - shown} more`));
    els.renameAffected.append(list);
    if (live.length) {
      const names = [...new Set(live.map((session) => session.entry.name))].join(', ');
      els.renameAffected.append(element('p', 'warning', live.length === 1
        ? `Stop the session of ${names} first: a running session keeps the folder in use.`
        : `Stop the ${live.length} sessions running in it first (${names}): a running session keeps the folder in use.`));
    }
  }

  function submitRename() {
    if (els.renameOk.disabled || !renaming.path) return;
    const name = els.renameName.value.trim();
    if (!name) {
      els.renameName.focus();
      return;
    }
    setRenameBusy(true);
    setRenameStatus('Renaming…');
    post({ type: 'renameFolder', path: renaming.path, name, renameProject: !els.renameProjectOption.hidden && els.renameProject.checked });
  }

  els.renameOk.addEventListener('click', submitRename);
  $('rename-cancel').addEventListener('click', () => els.renameDialog.close());
  els.renameName.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    submitRename();
  });
  els.renameName.addEventListener('input', () => {
    setRenameStatus('');
    updateRenamePreview();
  });
  // The picker opens where the folder is, so that it can be picked (or another one next to it).
  $('rename-browse').addEventListener('click', () => post({ type: 'browse', purpose: 'rename', path: parentOf(renaming.path) }));
  els.renameDialog.addEventListener('close', () => setTimeout(refocusTerminal, 0));

  // ---- sessions -------------------------------------------------------------------------------

  /** Opens a tile, with a terminal of its own, for a session of a project; `id` is given when re-attaching. */
  function createSession(entry, id = newSessionId()) {
    const tile = element('article', 'tile');
    const head = element('header', 'tile-head');
    const jarvisChip = element('span', 'tile-jarvis', 'Jarvis');
    jarvisChip.title = 'The voice window: spoken requests from push-to-talk at the PC come here';
    const detail = element('span', 'tile-detail', entry.path);
    const stateText = element('span', 'tile-state');
    const actions = element('div', 'tile-actions');
    const maximizeButton = actionButton('maximize', ICONS.maximize, 'Maximize / restore (F11)');
    actions.append(
      actionButton('stop', ICONS.stop, 'Stop this session'),
      maximizeButton,
      actionButton('close', ICONS.close, 'Close'));
    const nameEl = element('span', 'tile-name', entry.name);
    head.append(element('span', 'dot'), nameEl, jarvisChip, detail);
    head.append(stateText, actions);
    head.title = entry.path;
    tile.classList.toggle('is-hub', isHub(entry.id));

    const body = element('div', 'tile-body');
    const termHost = element('div', 'term-host');
    const exitBar = element('div', 'exit-bar');
    const exitMessage = element('span', 'exit-message');
    const relaunch = element('button', 'primary', 'Launch');
    const resume = element('button', '', 'Resume conversation');
    resume.title = 'Launch with --continue to pick up the last conversation in this folder';
    const dismiss = element('button', '', 'Close');
    exitBar.append(exitMessage, relaunch, resume, dismiss);
    body.append(termHost, exitBar);
    tile.append(head, body);

    const options = {
      allowProposedApi: true,
      fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
      fontSize: state.settings.fontSize,
      cursorBlink: true,
      scrollback: 10000,
      theme: themeFor(colorOf(entry)),
      linkHandler: { activate: (_event, uri) => post({ type: 'openExternal', url: uri }) },
    };
    if (state.osBuild) options.windowsPty = { backend: 'conpty', buildNumber: state.osBuild };
    const term = new Terminal(options);
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.loadAddon(new Unicode11Addon.Unicode11Addon());
    term.unicode.activeVersion = '11';
    term.loadAddon(new WebLinksAddon.WebLinksAddon((_event, uri) => post({ type: 'openExternal', url: uri })));

    // Tiles of the same project are told apart by a number: the lowest one none of the others uses.
    const taken = new Set(sessionsOf(entry.id).map((other) => other.number));
    let number = 1;
    while (taken.has(number)) number++;

    const session = {
      id, entry, number, term, fit,
      run: 0, status: 'idle', exitCode: null, error: '', stopped: false, historyNote: '', trustWatch: null,
      title: '', busy: false, busySince: 0, lastOutputAt: 0, launchedAt: 0, attention: false,
      // waiting: done working (or rang) and nobody typed since; unlike attention also while watched here
      waiting: false,
      // hooked: Claude Code reports through hooks (busy and waiting come from them); jarvis: the voice tile
      hooked: false, hookSeen: false, jarvis: false, voiceQueue: [],
      stopRequested: false, ctrlVPending: false, fitTimer: 0,
      color: colorOf(entry), tintCarry: '',
      el: { tile, head, name: nameEl, detail, stateText, exitMessage, termHost, maximizeButton, resume },
    };
    state.sessions.set(id, session);

    // Tiles follow the order of the list (a project's own tiles by number).
    const ids = openSessionIds();
    const next = state.sessions.get(ids[ids.indexOf(id) + 1]);
    els.grid.insertBefore(tile, next ? next.el.tile : null);

    term.open(termHost);
    try {
      const webgl = new WebglAddon.WebglAddon();
      // Browsers cap the number of live WebGL contexts; a terminal that loses its context falls back to the DOM renderer.
      webgl.onContextLoss(() => webgl.dispose());
      term.loadAddon(webgl);
    } catch (error) {
      console.warn('WebGL renderer unavailable, using the DOM renderer.', error);
    }

    term.onData((data) => {
      if (session.status !== 'running') return;
      // Replies the terminal makes by itself (focus in/out, which Claude Code asks for, cursor position, device
      // attributes) are not someone at the PC: they go to the session but count as no typing here.
      if (TERMINAL_REPORT.test(data)) {
        post({ type: 'input', id: session.id, data });
        return;
      }
      post({ type: 'input', id: session.id, data });
      clearAttention(session);
      clearWaiting(session);
      if (data === '\x1b') checkInterrupted(session);
    });
    term.onResize(({ cols, rows }) => {
      if (session.status === 'running') post({ type: 'resize', id: session.id, cols, rows });
    });
    // Once the user is at the keyboard, prompts are theirs to answer.
    term.onKey(() => { session.trustWatch = null; });
    term.onTitleChange((title) => { session.title = title; refresh(session.id); });
    term.onBell(() => {
      if (session.hooked) return; // its hooks say when it waits
      setWaiting(session);
      raiseAttention(session);
    });
    term.attachCustomKeyEventHandler((event) => handleTerminalKey(session, event));
    term.textarea.addEventListener('focus', () => setActive(session.id));

    termHost.addEventListener('contextmenu', (event) => {
      // Console convention: right click copies the selection, or pastes when there is none.
      event.preventDefault();
      if (term.hasSelection()) copySelection(session);
      else post({ type: 'paste', id: session.id });
    });
    tile.addEventListener('mousedown', (event) => {
      // Inside the terminal xterm.js handles focus and selection itself. Everywhere else the default
      // action would move focus off the terminal, so it is suppressed (clicks still fire).
      const inTerminal = !!event.target.closest('.term-host');
      if (!inTerminal) event.preventDefault();
      if (!event.target.closest('button')) activate(session.id, { focus: !inTerminal });
    });
    head.addEventListener('dblclick', (event) => {
      if (!event.target.closest('button')) toggleMaximize(session.id);
    });
    actions.addEventListener('click', (event) => {
      const button = event.target.closest('button[data-act]');
      if (!button) return;
      switch (button.dataset.act) {
        case 'stop': confirmThen(button, () => stop(session.id)); break;
        case 'maximize': toggleMaximize(session.id); break;
        case 'close':
          if (isLive(session)) confirmThen(button, () => closeTile(session.id));
          else closeTile(session.id);
          break;
      }
    });
    relaunch.addEventListener('click', () => launch(session.id));
    resume.addEventListener('click', () => launch(session.id, { resume: true }));
    dismiss.addEventListener('click', () => closeTile(session.id));

    session.resizeObserver = new ResizeObserver(() => scheduleFit(session));
    session.resizeObserver.observe(body);
    return session;
  }

  /** Jarvis's tile (in All projects): spoken requests go there, and only there. */
  function setJarvis(session, on) {
    session.jarvis = on;
    session.el.tile.classList.toggle('is-jarvis', on);
  }

  /** Types a line into a session as if at its keyboard, then Enter (a moment later, so it is not taken for a paste). */
  function typeLine(session, text) {
    post({ type: 'input', id: session.id, data: text });
    setTimeout(() => { if (session.status === 'running') post({ type: 'input', id: session.id, data: '\r' }); }, 60);
  }

  /** Voice requests that arrived while Jarvis's tile was starting go in once it has shown it is up. */
  function flushVoice(session) {
    if (session.voiceQueue.length && session.status === 'running') typeLine(session, session.voiceQueue.splice(0).join(' '));
  }

  /**
   * Starts a session of a project from the list: in one of its tiles whose session has ended (the
   * active one first), or else in a new tile, next to the sessions of it that are still running.
   */
  function launchEntry(entryId, { resume = false, prompt = '', quiet = false } = {}) {
    if (!findEntry(entryId) || state.relocating.has(entryId)) return;
    const ended = sessionsOf(entryId).filter((session) => !isLive(session) && !session.jarvis); // Jarvis's tile is kept for Jarvis
    const rank = (session) => (session.id === state.activeId ? 0 : 1);
    const session = ended.sort((a, b) => rank(a) - rank(b))[0] || createSession(findEntry(entryId));
    launch(session.id, { resume, prompt, quiet });
    return session;
  }

  /** Starts the session of a tile (again). */
  function launch(id, { resume = false, prompt = '', quiet = false } = {}) {
    const session = state.sessions.get(id);
    if (!session || !findEntry(session.entry.id)) return;
    if (isLive(session)) {
      if (!quiet) activate(id);
      return;
    }
    if (session.status !== 'idle') session.term.reset(); // a new tile has nothing on it yet

    Object.assign(session, {
      status: 'starting', run: 0, exitCode: null, error: '', title: '', stopped: false, historyNote: '',
      busy: false, attention: false, waiting: false, stopRequested: false, launchedAt: performance.now(), tintCarry: '',
      hooked: false, hookSeen: false,
      trustWatch: state.settings.autoTrust ? { lastKeyAt: 0, moves: 0 } : null,
    });
    layoutGrid();
    if (!quiet) activate(id); // a voice request must not take the keyboard from whoever types at the PC
    fitNow(session);
    post({
      type: 'launch', id, entry: session.entry.id, cols: session.term.cols, rows: session.term.rows, resume,
      // The first prompt: a voice request in Jarvis's tile, or one the API (maidctl, Jarvis) starts the session with.
      jarvis: session.jarvis, prompt,
    });
  }

  function stop(id) {
    const session = state.sessions.get(id);
    if (!session || session.status !== 'running') return;
    session.stopRequested = true;
    post({ type: 'kill', id });
    refresh(id);
  }

  function closeTile(id) {
    const session = state.sessions.get(id);
    if (!session) return;
    if (isLive(session)) post({ type: 'kill', id });
    disposeSession(id);
  }

  function disposeSession(id) {
    const session = state.sessions.get(id);
    if (!session) return;
    clearTimeout(session.fitTimer);
    session.resizeObserver.disconnect();
    session.term.dispose();
    session.el.tile.remove();
    state.sessions.delete(id);
    if (state.maximizedId === id) state.maximizedId = null;
    if (state.activeId === id) {
      state.activeId = null;
      const neighbour = openSessionIds()[0];
      if (neighbour) activate(neighbour);
    }
    layoutGrid();
    refreshEntry(session.entry.id);
  }

  // ---- focus, maximize, layout ----------------------------------------------------------------

  /** Ids of open sessions in on-screen order (the order of the list). */
  function openSessionIds() {
    const ids = [];
    for (const entry of orderedEntries()) for (const session of sessionsOf(entry.id)) ids.push(session.id);
    return ids;
  }

  /** Puts the tiles in list order after entries moved between groups. */
  function reorderTiles() {
    const tiles = openSessionIds().map((id) => state.sessions.get(id).el.tile);
    if (tiles.every((tile, index) => els.grid.children[index] === tile)) return;
    // Moving a node in the DOM drops keyboard focus if it holds it.
    const focused = document.activeElement;
    for (const tile of tiles) els.grid.append(tile);
    if (focused && focused !== document.activeElement && focused.isConnected) focused.focus();
  }

  function setActive(id) {
    if (state.activeId === id) {
      const current = state.sessions.get(id);
      if (current) clearAttention(current);
      return;
    }
    const previous = state.activeId;
    state.activeId = id;
    const session = state.sessions.get(id);
    if (session) clearAttention(session);
    refresh(previous);
    refresh(id);
  }

  function activate(id, { focus = true } = {}) {
    const session = state.sessions.get(id);
    if (!session) return;
    if (state.maximizedId && state.maximizedId !== id) {
      state.maximizedId = id;
      layoutGrid();
    }
    setActive(id);
    if (focus) session.term.focus();
  }

  function toggleMaximize(id) {
    if (!id || !state.sessions.has(id)) return;
    state.maximizedId = state.maximizedId === id ? null : id;
    layoutGrid();
    activate(id);
  }

  /** Brings up a session of a project; clicking its row again moves on to the next one. */
  function activateEntry(entryId) {
    const sessions = sessionsOf(entryId);
    if (!sessions.length) return;
    const index = sessions.findIndex((session) => session.id === state.activeId);
    activate(sessions[(index + 1) % sessions.length].id);
  }

  function cycle(step) {
    const ids = openSessionIds();
    if (!ids.length) return;
    const index = ids.indexOf(state.activeId);
    activate(ids[(index + step + ids.length) % ids.length]);
  }

  function autoColumns(count) {
    const { width, height } = els.stage.getBoundingClientRect();
    let best = 1;
    let bestScore = Infinity;
    for (let columns = 1; columns <= count; columns++) {
      const rows = Math.ceil(count / columns);
      const aspect = (width / columns) / Math.max(1, height / rows);
      const score = Math.abs(Math.log(aspect / 1.45)); // terminals read best a bit wider than tall
      if (score < bestScore) { best = columns; bestScore = score; }
    }
    return best;
  }

  function layoutGrid() {
    const ids = openSessionIds();
    const count = ids.length;
    const maximized = state.maximizedId && state.sessions.has(state.maximizedId);
    els.grid.classList.toggle('maximized', !!maximized);

    if (count && !maximized) {
      const columns = state.settings.columns > 0 ? Math.min(state.settings.columns, count) : autoColumns(count);
      const rows = Math.ceil(count / columns);
      // Tiles of a partly filled last row stretch to share the full width: lay the grid out on
      // (columns x lastRow) tracks and let each tile span accordingly.
      const lastRow = count - (rows - 1) * columns;
      const stretch = lastRow !== columns;
      els.grid.style.gridTemplateColumns = `repeat(${stretch ? columns * lastRow : columns}, minmax(0, 1fr))`;
      els.grid.style.gridTemplateRows = `repeat(${rows}, minmax(0, 1fr))`;
      ids.forEach((id, index) => {
        const span = !stretch ? 1 : index < count - lastRow ? lastRow : columns;
        state.sessions.get(id).el.tile.style.gridColumn = `span ${span}`;
      });
    } else {
      els.grid.style.gridTemplateColumns = 'minmax(0, 1fr)';
      els.grid.style.gridTemplateRows = 'minmax(0, 1fr)';
      ids.forEach((id) => { state.sessions.get(id).el.tile.style.gridColumn = ''; });
    }

    for (const id of ids) refresh(id);
    updateSummary();
  }

  function scheduleFit(session) {
    clearTimeout(session.fitTimer);
    session.fitTimer = setTimeout(() => fitNow(session), 40);
  }

  function fitNow(session) {
    if (!session.el.termHost.offsetParent) return; // hidden behind a maximized tile
    const size = session.fit.proposeDimensions();
    if (!size || !Number.isFinite(size.cols) || !Number.isFinite(size.rows)) return;
    const cols = Math.max(20, size.cols);
    const rows = Math.max(4, size.rows);
    if (cols !== session.term.cols || rows !== session.term.rows) session.term.resize(cols, rows);
  }

  // ---- status ---------------------------------------------------------------------------------

  function isWatching(session) {
    return document.hasFocus() && state.activeId === session.id && !!session.el.termHost.offsetParent;
  }

  function raiseAttention(session) {
    if (session.attention || session.status !== 'running' || isWatching(session)) return;
    session.attention = true;
    refresh(session.id);
    updateSummary();
  }

  function clearAttention(session) {
    if (!session.attention) return;
    session.attention = false;
    refresh(session.id);
    updateSummary();
  }

  /**
   * Esc stops Claude Code mid-turn without a Stop hook. Once the "esc to interrupt" hint is gone from its
   * status line, it no longer works (if the hint stays, Esc did something else and it carries on).
   */
  function checkInterrupted(session) {
    if (!session.hooked || !session.busy) return;
    setTimeout(() => {
      if (!session.busy || session.status !== 'running') return;
      const buffer = session.term.buffer.active;
      for (let y = buffer.baseY; y < buffer.baseY + session.term.rows; y++) {
        const line = buffer.getLine(y);
        if (line && /esc to interrupt/i.test(line.translateToString(true))) return;
      }
      setBusy(session, false);
      post({ type: 'interrupted', id: session.id }); // the API's waits end with it
    }, 1500);
  }

  /** Working or not, as Claude Code's hooks say. */
  function setBusy(session, on) {
    if (session.busy === on) return;
    session.busy = on;
    if (on) session.busySince = performance.now();
    refresh(session.id);
    publishTiles();
  }

  function noteOutput(session) {
    const now = performance.now();
    session.lastOutputAt = now;
    if (session.hooked) return;
    if (!session.busy) {
      session.busy = true;
      session.busySince = now;
      refresh(session.id);
    }
  }

  setInterval(() => {
    const now = performance.now();
    for (const session of state.sessions.values()) {
      if (session.hooked || !session.busy || now - session.lastOutputAt < QUIET_MS) continue;
      session.busy = false;
      const worked = session.lastOutputAt - session.busySince;
      if (worked >= MIN_WORK_MS && session.busySince - session.launchedAt > STARTUP_GRACE_MS) {
        setWaiting(session);
        raiseAttention(session);
      }
      refresh(session.id);
    }
  }, 300);

  // ---- trust prompt ---------------------------------------------------------------------------

  /**
   * Looks for Claude Code's "do you trust this folder?" prompt on the screen. Returns how many rows
   * the selection caret is away from the "Yes" option (0 = on it), or null when the prompt is not up.
   * Works on the rendered screen, so it does not depend on how the TUI paints it.
   */
  function findTrustPrompt(term) {
    const buffer = term.buffer.active;
    const lines = [];
    for (let y = buffer.baseY; y < buffer.baseY + term.rows; y++) {
      const line = buffer.getLine(y);
      lines.push(line ? line.translateToString(true) : '');
    }
    // "Yes, I trust this folder" today, "1. Yes, proceed" / "Yes, continue" / "Yes, allow …" in older versions.
    const yes = lines.findIndex((line) => /^\s*(?:[❯>›]\s*)?(?:\d+\.\s*)?Yes,? (?:I trust|proceed|continue|allow)\b/i.test(line));
    if (yes < 0 || !lines.some((line) => /trust/i.test(line))) return null;
    const caret = lines.findIndex((line, index) => Math.abs(index - yes) <= 3 && /^\s*[❯>›]\s*\S/.test(line));
    return caret < 0 ? null : { offset: yes - caret };
  }

  setInterval(() => {
    const now = performance.now();
    for (const session of state.sessions.values()) {
      const watch = session.trustWatch;
      if (!watch || session.status !== 'running') continue;
      if (now - session.launchedAt > TRUST_WATCH_MS || watch.moves > 6) {
        session.trustWatch = null; // not asked, or the caret does not follow: leave it to the user
        continue;
      }
      // One key at a time; the screen is read again before the next, so Enter is only ever sent
      // while the caret is seen resting on "Yes".
      if (now - watch.lastKeyAt < TRUST_KEY_SPACING_MS) continue;
      const prompt = findTrustPrompt(session.term);
      if (!prompt) continue;

      const arrow = (session.term.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[') + (prompt.offset > 0 ? 'B' : 'A');
      post({ type: 'input', id: session.id, data: prompt.offset === 0 ? '\r' : arrow });
      watch.lastKeyAt = now;
      if (prompt.offset === 0) session.trustWatch = null;
      else watch.moves++;
    }
  }, 150);

  function describe(session) {
    switch (session.status) {
      case 'starting': return 'Starting…';
      case 'running':
        if (session.stopRequested) return 'Stopping…';
        if (session.attention) return 'Waiting for you';
        return session.busy ? 'Working…' : '';
      case 'exited':
        if (session.stopped) return 'Stopped';
        return session.exitCode === 0 ? 'Ended' : `Exited (${session.exitCode})`;
      default: return 'Failed to start';
    }
  }

  const isFailed = (session) => session.status === 'failed' || (session.status === 'exited' && session.exitCode !== 0 && !session.stopped);

  /** Syncs one session's tile, and the sidebar row and group header of its project, with the state. */
  function refresh(id) {
    const session = id && state.sessions.get(id);
    if (!session) return;
    const running = session.status === 'running';
    const flags = {
      'is-open': true,
      'is-starting': session.status === 'starting',
      'is-running': running,
      'is-busy': running && session.busy && !session.attention,
      'is-attention': running && session.attention,
      'is-exited': !isLive(session),
      'is-failed': isFailed(session),
      'is-active': state.activeId === id,
      'is-maximized': state.maximizedId === id,
    };
    for (const [name, on] of Object.entries(flags)) session.el.tile.classList.toggle(name, on);

    session.el.detail.textContent = session.title || session.entry.path;
    session.el.head.title = session.entry.path;
    session.el.stateText.textContent = describe(session);
    const ending = session.stopped ? 'Session stopped.' : `Session ended (exit code ${session.exitCode}).`;
    session.el.exitMessage.textContent = session.status === 'failed' ? session.error : `${ending} ${session.historyNote}`.trim();
    session.el.exitMessage.title = session.el.exitMessage.textContent;
    session.el.maximizeButton.innerHTML = flags['is-maximized'] ? ICONS.restore : ICONS.maximize;
    refreshEntry(session.entry.id);
  }

  /** Syncs a project's sidebar row (a summary of all its sessions), its group header and its tiles' names. */
  function refreshEntry(entryId) {
    const entry = findEntry(entryId);
    const row = state.rows.get(entryId);
    const sessions = sessionsOf(entryId);
    const live = sessions.filter(isLive);
    const running = sessions.filter((session) => session.status === 'running');
    const attention = running.some((session) => session.attention);
    const flags = {
      'is-open': sessions.length > 0,
      'is-starting': live.length > 0 && !running.length,
      'is-running': running.length > 0,
      'is-busy': !attention && running.some((session) => session.busy),
      'is-attention': attention,
      'is-exited': sessions.length > 0 && !live.length,
      'is-failed': sessions.length > 0 && !live.length && sessions.some(isFailed),
      'is-active': sessions.some((session) => session.id === state.activeId),
      'is-maximized': sessions.some((session) => session.id === state.maximizedId),
    };
    if (row) {
      const moving = state.relocating.get(entryId);
      for (const [name, on] of Object.entries(flags)) row.row.classList.toggle(name, on);
      row.row.classList.toggle('is-moving', !!moving);
      row.detail.textContent = '‎' + (moving ? `Moving to ${moving.path || 'another folder'}…${moving.files ? ` (${moving.files.toLocaleString()} files)` : ''}`
        : attention ? 'Done — waiting for you' : entry ? entry.path : '');
      row.count.textContent = live.length > 1 ? String(live.length) : '';
      row.count.hidden = live.length < 2;
      row.launchButton.title = live.length ? 'Start another session in this project' : 'Launch';
    }
    if (isHub(entryId)) {
      // The toolbar button stands in for a row.
      for (const [name, on] of Object.entries(flags)) els.hubButton.classList.toggle(name, on);
      els.hubCount.textContent = live.length > 1 ? String(live.length) : '';
      els.hubCount.hidden = live.length < 2;
    }
    // The headers of the groups its group is nested in count it too.
    if (entry) for (const group of lineageOf(entry.group)) refreshGroup(group.name);
    // A project's tiles carry numbers while there is more than one of them.
    for (const session of sessions)
      session.el.name.textContent = sessions.length > 1 ? `${session.entry.name} #${session.number}` : session.entry.name;
    publishTiles();
  }

  function updateSummary() {
    const sessions = [...state.sessions.values()];
    const running = sessions.filter((session) => session.status === 'running').length;
    const waiting = sessions.filter((session) => session.attention).length;

    els.sideSummary.textContent = running ? `${running} running` : '';
    els.sideEmpty.hidden = state.entries.length > 0;
    els.sideEmpty.textContent = state.editing
      ? 'Projects you add are listed here, grouped under labels of your choice. Press ▶ next to one to start Claude Code in it.'
      : 'No projects yet. Turn on Edit (top right) to add one.';
    els.emptyHint.innerHTML = state.entries.length
      ? 'Press <b>▶</b> next to a project in the list to start Claude Code in it. ' +
        '<b>All projects</b> (top right) starts a session that works on all of them, e.g. on the instructions they share.'
      : (state.editing
        ? '<b>Add</b> a project folder above, then start it from the list.'
        : 'Turn on <b>Edit</b> (top right, Ctrl+Shift+E) to add a project folder, then start it from the list.')
        + ' New to Maid? <b>?</b> at the top right (F1) shows around.';
    // Projects are started from the list, so it may only be hidden while there is something else to look at.
    document.body.classList.toggle('has-tiles', sessions.length > 0);
    els.emptyCommand.textContent = state.settings.command;

    // The window shows this after "Administrator: " and puts Maid's version after "Maid" (WindowTitle in MainWindow.xaml.cs).
    let title = 'Maid';
    if (running) title += ` — ${running} session${running === 1 ? '' : 's'}`;
    if (waiting) title += ` · ${waiting} waiting for you`;
    document.title = title;
  }

  // ---- keyboard and clipboard -----------------------------------------------------------------

  function copySelection(session) {
    const text = session.term.getSelection();
    if (text) post({ type: 'copy', text });
    session.term.clearSelection();
  }

  /** Returning false keeps xterm.js from handling the key. */
  function handleTerminalKey(session, event) {
    if (event.type !== 'keydown') return true;
    const key = event.key.toLowerCase();
    const ctrl = event.ctrlKey && !event.altKey && !event.metaKey;

    // Ctrl+C copies when there is a selection (like Windows Terminal), otherwise it is ^C for the app.
    if (ctrl && key === 'c' && (event.shiftKey || session.term.hasSelection())) {
      event.preventDefault();
      copySelection(session);
      return false;
    }
    if (ctrl && key === 'v') {
      event.preventDefault();
      session.ctrlVPending = !event.shiftKey;
      post({ type: 'paste', id: session.id });
      return false;
    }
    // Shift+Enter inserts a newline in Claude Code's prompt (sent as Meta+Enter, which it understands).
    if (event.key === 'Enter' && event.shiftKey && !event.ctrlKey && !event.altKey) {
      event.preventDefault();
      if (session.status === 'running') post({ type: 'input', id: session.id, data: '\x1b\r' });
      return false;
    }
    return true;
  }

  // Dashboard shortcuts win over the terminal, so they are taken in the capture phase. They use
  // physical keys where the character depends on the keyboard layout.
  window.addEventListener('keydown', (event) => {
    if (modalOpen()) return; // the dialog has the keyboard (Enter confirms, Escape cancels)
    const ctrl = event.ctrlKey && !event.altKey && !event.metaKey;
    const key = event.key.toLowerCase();
    let handled = true;

    if (event.key === 'Escape' && drag.active) cancelDrag();
    else if (event.key === 'Escape' && !els.groupMenu.hidden) closeMenu();
    else if (ctrl && event.key === 'Tab') cycle(event.shiftKey ? -1 : 1);
    else if (ctrl && !event.shiftKey && /^Digit[1-9]$/.test(event.code)) {
      const id = openSessionIds()[Number(event.code.slice(5)) - 1];
      if (id) activate(id);
    }
    else if (event.key === 'F11' && !ctrl && !event.altKey) toggleMaximize(state.activeId);
    else if (event.key === 'F1' && !ctrl && !event.altKey && !event.shiftKey) openHelp();
    else if (ctrl && event.shiftKey && key === 'n') { setEditing(true); els.pathInput.focus(); els.pathInput.select(); }
    else if (ctrl && event.shiftKey && key === 'e') setEditing(!state.editing);
    else if (ctrl && event.shiftKey && key === 'b') toggleSidebar();
    else if (ctrl && !event.shiftKey && (event.code === 'Equal' || event.code === 'NumpadAdd')) setFontSize(state.settings.fontSize + 1);
    else if (ctrl && !event.shiftKey && (event.code === 'Minus' || event.code === 'NumpadSubtract')) setFontSize(state.settings.fontSize - 1);
    else if (ctrl && !event.shiftKey && (event.code === 'Digit0' || event.code === 'Numpad0')) setFontSize(FONT_SIZES.fallback);
    else handled = false;

    if (handled) {
      event.preventDefault();
      event.stopPropagation();
    }
  }, true);

  // A click anywhere else closes the menu and settles an open group-name editor.
  window.addEventListener('mousedown', (event) => {
    if (!els.groupMenu.hidden && !els.groupMenu.contains(event.target)) closeMenu();
    if (state.editor && !state.editor.contains(event.target)) state.editor.finish(true);
  }, true);

  window.addEventListener('focus', () => {
    const session = state.sessions.get(state.activeId);
    if (session && session.el.termHost.offsetParent) clearAttention(session);
  });

  // ---- settings -------------------------------------------------------------------------------

  function setFontSize(size) {
    size = Math.min(FONT_SIZES.max, Math.max(FONT_SIZES.min, size));
    if (size === state.settings.fontSize) return;
    state.settings.fontSize = size;
    for (const session of state.sessions.values()) {
      session.term.options.fontSize = size;
      fitNow(session);
    }
    post({ type: 'settings', fontSize: size });
  }

  function setColumns(columns, { save = true } = {}) {
    state.settings.columns = columns;
    for (const button of els.columns.querySelectorAll('button'))
      button.classList.toggle('selected', Number(button.dataset.columns) === columns);
    // A fixed column count only makes sense for the grid, so picking one leaves the maximized view.
    if (save) state.maximizedId = null;
    layoutGrid();
    if (save) post({ type: 'settings', columns });
  }

  /**
   * Shows or hides the list's editing controls (add form, group and project menus, drag). Day to day
   * the list is only used to run projects, so this is off whenever Maid starts and is never saved.
   */
  function setEditing(on) {
    if (state.editing === on) return;
    state.editing = on;
    document.body.classList.toggle('editing', on);
    els.editToggle.classList.toggle('on', on);
    els.editToggle.setAttribute('aria-pressed', String(on));
    if (!on) {
      cancelDrag();
      closeMenu();
      if (state.editor) state.editor.finish(true);
      showFormError('');
    }
    updateSummary();
  }

  function toggleSidebar(collapsed = !state.settings.sidebarCollapsed, { save = true } = {}) {
    state.settings.sidebarCollapsed = collapsed;
    document.body.classList.toggle('sidebar-collapsed', collapsed);
    if (collapsed) closeMenu();
    if (save) post({ type: 'settings', sidebarCollapsed: collapsed });
  }

  /** The list's width, kept within its limits and, while the window is narrow, leaving the terminals room. */
  function setSidebarWidth(width, { save = true } = {}) {
    const room = Math.max(SIDEBAR_WIDTH.min, window.innerWidth - 320);
    const pixels = Math.round(Math.min(Math.max(Number(width) || SIDEBAR_WIDTH.usual, SIDEBAR_WIDTH.min), SIDEBAR_WIDTH.max, room));
    state.settings.sidebarWidth = pixels;
    els.sidebar.style.width = `${pixels}px`;
    if (save) post({ type: 'settings', sidebarWidth: pixels });
  }

  // Dragging the list's right edge resizes it; the width is saved when the drag ends. Double-click: the usual width.
  {
    const resizer = $('sidebar-resizer');
    let drag = null;
    resizer.addEventListener('pointerdown', (event) => {
      if (event.button !== 0) return;
      event.preventDefault();
      resizer.setPointerCapture(event.pointerId);
      drag = { x: event.clientX, width: els.sidebar.getBoundingClientRect().width };
      document.body.classList.add('resizing-sidebar');
    });
    resizer.addEventListener('pointermove', (event) => {
      if (drag) setSidebarWidth(drag.width + event.clientX - drag.x, { save: false });
    });
    const finish = () => {
      if (!drag) return;
      drag = null;
      document.body.classList.remove('resizing-sidebar');
      setSidebarWidth(state.settings.sidebarWidth);
      setTimeout(refocusTerminal, 0);
    };
    resizer.addEventListener('pointerup', finish);
    resizer.addEventListener('pointercancel', finish);
    resizer.addEventListener('dblclick', () => {
      setSidebarWidth(SIDEBAR_WIDTH.usual);
      setTimeout(refocusTerminal, 0);
    });
  }

  // ---- top bar --------------------------------------------------------------------------------

  let formNoteTimer = 0;

  function showFormError(message, { note = false } = {}) {
    clearTimeout(formNoteTimer);
    els.formError.textContent = message;
    els.formError.hidden = !message;
    els.formError.classList.toggle('note', note);
    els.pathField.classList.toggle('invalid', !!message && !note);
  }

  /** A passing remark in the same place as form errors. */
  function showFormNote(message) {
    showFormError(message, { note: true });
    formNoteTimer = setTimeout(() => showFormError(''), 4000);
  }

  /** An error that is not about the path box: shown for a while, without marking the box. */
  function flashError(message) {
    showFormError(message);
    els.pathField.classList.remove('invalid');
    formNoteTimer = setTimeout(() => showFormError(''), 6000);
  }

  els.form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (!state.editing) return;
    const path = els.pathInput.value.trim();
    if (!path) {
      els.pathInput.focus();
      return;
    }
    showFormError('');
    post({ type: 'create', path, group: els.groupInput.value.trim() });
  });
  els.pathInput.addEventListener('input', () => showFormError(''));
  $('browse-btn').addEventListener('click', () => post({ type: 'browse', path: els.pathInput.value }));
  $('sidebar-toggle').addEventListener('click', () => toggleSidebar());
  els.editToggle.addEventListener('click', () => setEditing(!state.editing));
  $('info-btn').addEventListener('click', () => openInfoDialog());
  els.hubButton.addEventListener('click', () => { if (state.hub) launchEntry(state.hub.id); });
  $('add-group').addEventListener('click', () => startNewGroup());
  $('font-smaller').addEventListener('click', () => setFontSize(state.settings.fontSize - 1));
  $('font-larger').addEventListener('click', () => setFontSize(state.settings.fontSize + 1));
  els.columns.addEventListener('click', (event) => {
    const button = event.target.closest('button[data-columns]');
    if (button) setColumns(Number(button.dataset.columns));
  });
  // Using the list, the menu or the toolbar should not take keyboard focus away from the active
  // terminal; text boxes in them (group names) are the exception.
  for (const chrome of [els.sidebar, els.groupMenu, document.querySelector('.toolbar'), $('sidebar-toggle')])
    chrome.addEventListener('mousedown', (event) => { if (!event.target.closest('input')) event.preventDefault(); });

  let relayout = 0;
  new ResizeObserver(() => {
    // The automatic layout depends on the shape of the stage.
    cancelAnimationFrame(relayout);
    relayout = requestAnimationFrame(() => { if (!state.settings.columns) layoutGrid(); });
  }).observe(els.stage);

  // ---- the tiles, for the host ---------------------------------------------------------------

  // The host knows the tiles from what the page sends here (Maid's API finds its windows there), and the page
  // does what the host asks for (`remote`), as if its buttons had been clicked.

  let tilesTimer = 0;
  let tilesSent = '';

  /** Tells the host which tiles there are, in their order, and how their sessions are doing. */
  function publishTiles() {
    if (tilesTimer) return;
    tilesTimer = setTimeout(() => {
      tilesTimer = 0;
      const tiles = openSessionIds().map((id) => {
        const session = state.sessions.get(id);
        const entry = session.entry;
        const running = session.status === 'running';
        return {
          id, entry: entry.id, name: sessionsOf(entry.id).length > 1 ? `${entry.name} #${session.number}` : entry.name, number: session.number,
          jarvis: session.jarvis,
          project: entry.name, path: entry.path, group: isHub(entry.id) ? '' : groupPath(entry.group), hub: isHub(entry.id),
          color: session.color || '', status: session.status, title: session.title,
          busy: running && session.busy && !session.waiting, waiting: running && session.waiting, stopping: running && session.stopRequested,
          exitCode: session.exitCode, stopped: session.stopped, failed: isFailed(session),
          message: session.status === 'failed' ? session.error : session.historyNote,
        };
      });
      const json = JSON.stringify(tiles);
      if (json === tilesSent) return;
      tilesSent = json;
      post({ type: 'tiles', tiles });
    }, 120);
  }

  /** Done working (or rang the bell) and nobody has typed since. */
  function setWaiting(session) {
    if (session.waiting || session.status !== 'running') return;
    session.waiting = true;
    publishTiles();
  }

  function clearWaiting(session) {
    if (!session.waiting) return;
    session.waiting = false;
    publishTiles();
  }

  // ---- help ------------------------------------------------------------------------------------

  /** What the help shows about this PC (from init): Maid's version, its settings file, whether Claude Code is there. */
  const help = { version: '', stateFile: '', claude: null, addAfter: false };
  const CLAUDE_CODE_URL = 'https://claude.com/claude-code';

  // The icons in the help text are the buttons' own.
  for (const slot of els.helpDialog.querySelectorAll('i[data-icon]')) slot.innerHTML = ICONS[slot.dataset.icon] || '';

  /** The program of a command line: its first word, or the quoted path it starts with. */
  const programOf = (command) => { const match = /^\s*(?:"([^"]+)"|(\S+))/.exec(command || ''); return match ? match[1] || match[2] : ''; };

  function renderHelp() {
    const command = state.settings.command || 'claude --dangerously-skip-permissions';
    $('help-version').textContent = help.version ? `Maid ${help.version}` : '';
    $('help-command').textContent = command;
    for (const span of els.helpDialog.querySelectorAll('[data-unless-command]'))
      span.hidden = !command.includes(span.dataset.unlessCommand);
    for (const item of els.helpDialog.querySelectorAll('[data-setting]'))
      item.hidden = state.settings[item.dataset.setting] === false;
    $('help-state').textContent = help.stateFile || 'state.json';

    // Sessions need Claude Code: say so plainly when its program is not on the PATH Maid was started with.
    const claude = help.claude;
    const missing = !!claude && !claude.path;
    els.helpClaude.hidden = !missing;
    if (missing) {
      const program = programOf(claude.command) || 'claude';
      const link = element('button', 'link-btn', 'claude.com/claude-code');
      link.type = 'button';
      link.addEventListener('click', () => post({ type: 'openExternal', url: CLAUDE_CODE_URL }));
      els.helpClaude.replaceChildren(
        element('b', '', 'Claude Code was not found. '),
        `Sessions start "${program}", which is not on this PC's PATH. Install Claude Code from `, link,
        ', sign in once in a terminal (run "claude"), then start Maid again.');
    }
    $('help-foot').textContent = claude && claude.path ? `Sessions start ${claude.path}` : '';
  }

  function openHelp() {
    if (modalOpen()) return;
    closeMenu();
    renderHelp();
    els.helpDialog.showModal();
    els.helpScroll.scrollTop = 0;
    $('help-close').focus();
  }

  $('help-btn').addEventListener('click', openHelp);
  $('help-close').addEventListener('click', () => els.helpDialog.close());
  $('help-add').addEventListener('click', () => {
    help.addAfter = true;
    els.helpDialog.close();
  });
  els.helpDialog.addEventListener('close', () => {
    // Seen once, it no longer opens by itself.
    if (!state.settings.helpSeen) {
      state.settings.helpSeen = true;
      post({ type: 'settings', helpSeen: true });
    }
    setTimeout(() => {
      if (!help.addAfter) return refocusTerminal();
      help.addAfter = false;
      setEditing(true);
      els.pathInput.focus();
    }, 0);
  });

  // ---- messages from the host -----------------------------------------------------------------

  const handlers = {
    init(message) {
      state.entries = message.entries || [];
      state.groups = message.groups || [];
      state.sharedInfo = message.sharedInfo || '';
      Object.assign(state.settings, message.settings || {});
      state.osBuild = message.osBuild || 0;
      state.hub = message.hub ? { id: message.hub.id, name: message.hub.name, path: message.hub.path, group: '' } : null;
      els.hubButton.hidden = !state.hub;
      if (state.hub) els.hubButton.title += `\n${state.hub.path}`;
      state.relocating = new Map((message.relocating || []).map((id) => [id, { path: '', files: 0 }]));
      renderSidebar();
      toggleSidebar(!!state.settings.sidebarCollapsed, { save: false });
      setSidebarWidth(state.settings.sidebarWidth, { save: false });
      setColumns(state.settings.columns || 0, { save: false });

      // After a page reload the processes are still there; attach fresh terminals to them.
      for (const running of message.running || []) {
        const entry = findEntry(running.entry);
        if (!entry) continue;
        const session = createSession(entry, running.id);
        Object.assign(session, { status: 'running', run: running.run, launchedAt: performance.now(), hooked: !!running.hooks, hookSeen: true });
        setJarvis(session, !!running.jarvis);
      }
      layoutGrid();
      for (const session of state.sessions.values()) {
        fitNow(session);
        repaint(session); // the new terminal is empty; the process still has its screen
      }
      const first = openSessionIds()[0];
      if (first) activate(first);
      else if (state.editing) els.pathInput.focus();
      if (message.elevationFailure)
        flashError(`Not running as administrator (${message.elevationFailure}): sessions start without admin rights. Restart Maid to try again.`);

      help.version = message.version || '';
      help.stateFile = message.stateFile || '';
      help.claude = message.claude || null;
      // Someone new (nothing listed yet) is shown around once; the help stays one key (F1) away.
      if (!state.settings.helpSeen && !state.entries.length && !els.helpDialog.open) openHelp();
    },

    /** The saved list changed (entry added or moved, group added, renamed or removed). */
    list(message) {
      state.entries = message.entries || [];
      state.groups = message.groups || [];
      for (const session of state.sessions.values()) {
        const entry = findEntry(session.entry.id);
        if (entry) session.entry = entry;
        applyGroupColor(session); // the group's colour changed, or the project moved to another group
      }
      renderSidebar();
      // A tile shows its project's folder, which a move or a rename changes.
      for (const id of state.sessions.keys()) refresh(id);
      reorderTiles();
      layoutGrid();
      if (els.renameDialog.open) updateRenamePreview();
    },

    browseResult(message) {
      if (message.purpose === 'rename') {
        if (!els.renameDialog.open) return;
        // A folder above the project's keeps the project's path on show.
        showRenameFolder(inFolder(renaming.full, message.path) ? renaming.full : message.path, message.path);
        return;
      }
      if (message.purpose === 'move') {
        // The folder picked is where the project goes into; its folder keeps its name (editable).
        const entry = findEntry(moveFor);
        if (!els.moveDialog.open || !entry) return;
        els.movePath.value = `${message.path.replace(/[\\/]+$/, '')}\\${folderName(entry.path)}`;
        setMoveStatus('');
        els.movePath.focus();
        return;
      }
      els.pathInput.value = message.path;
      showFormError('');
      els.pathInput.focus();
    },

    createFailed(message) {
      showFormError(message.message);
      els.pathInput.focus();
      els.pathInput.select();
    },

    entryReady(message) {
      // Adding a project only lists it (a "list" message delivered the new entry before this); it is
      // started from the list when it is needed.
      const entry = findEntry(message.entry.id) || message.entry;
      els.pathInput.value = '';
      // Keep the group for the next project; show it as the host spelled it.
      if (message.added && els.groupInput.value.trim()) els.groupInput.value = entry.group || '';
      if (state.settings.sidebarCollapsed) toggleSidebar(false);
      revealGroup(entry.group);
      const row = state.rows.get(entry.id);
      if (row) {
        row.row.scrollIntoView({ block: 'nearest' });
        row.row.classList.remove('is-new');
        void row.row.offsetWidth; // restart the highlight when the same row is pointed out again
        row.row.classList.add('is-new');
      }
      if (!message.added) showFormNote(`${entry.name} is already in the list.`);
    },

    entryRemoved(message) {
      for (const session of sessionsOf(message.id)) disposeSession(session.id);
      state.entries = state.entries.filter((entry) => entry.id !== message.id);
      renderSidebar();
      layoutGrid();
    },

    listFailed(message) {
      flashError(message.message);
    },

    /**
     * The shared info was stored and written to the projects' CLAUDE.md and AGENTS.md (those in `failed`
     * excepted) — saved here, or (source "hub") changed in the hub's shared-info.md.
     */
    sharedInfoSaved(message) {
      const previous = state.sharedInfo;
      state.sharedInfo = message.text || '';
      const fromHub = message.source === 'hub';
      if (els.infoDialog.open && !fromHub) els.infoDialog.close();
      else if (els.infoDialog.open && els.infoText.value === previous) {
        els.infoText.value = state.sharedInfo;
        setInfoStatus('Updated meanwhile in shared-info.md (the All projects session).');
      } else if (els.infoDialog.open) {
        setInfoStatus('Changed meanwhile in shared-info.md (the All projects session); saving here replaces that.');
      }
      const failed = message.failed || [];
      const changed = message.changed || 0;
      const what = fromHub ? 'Shared info changed in shared-info.md' : 'Shared info saved';
      if (failed.length) {
        flashError(`${what}, but it could not be written for ${failed.map((project) => `${project.name} (${project.message})`).join(', ')}.`);
      } else {
        showFormNote(changed
          ? `${what} — CLAUDE.md and AGENTS.md updated in ${changed} project${changed === 1 ? '' : 's'}.`
          : `${what}.`);
      }
    },

    /** A project's folder started moving (the list's row says so until it is done). */
    relocating(message) {
      state.relocating.set(message.id, { path: message.path, files: 0 });
      refreshEntry(message.id);
    },

    relocateProgress(message) {
      const moving = state.relocating.get(message.id);
      if (!moving) return;
      moving.files = message.files;
      refreshEntry(message.id);
      if (els.moveDialog.open && moveFor === message.id)
        setMoveStatus(`Copying to the other drive… ${message.files.toLocaleString()} files so far.`);
    },

    /** The folder moved; a "list" with the entry's new path came just before. */
    relocated(message) {
      state.relocating.delete(message.id);
      if (els.moveDialog.open && moveFor === message.id) els.moveDialog.close();
      refreshEntry(message.id);
      const entry = findEntry(message.id);
      if (message.warning) flashError(message.warning);
      else showFormNote(message.note || `${entry ? entry.name : 'The project'} moved to ${message.path}.`);
      const row = state.rows.get(message.id);
      if (row) row.row.scrollIntoView({ block: 'nearest' });
    },

    relocateFailed(message) {
      const wasMoving = state.relocating.delete(message.id);
      if (wasMoving) refreshEntry(message.id);
      if (els.moveDialog.open && moveFor === message.id) {
        setMoveBusy(false);
        setMoveStatus(message.message, { error: true });
        els.movePath.focus();
      } else {
        flashError(message.message);
      }
    },

    /** A folder was renamed; a "list" with the new paths of the projects in it came just before. */
    folderRenamed(message) {
      if (els.renameDialog.open) els.renameDialog.close();
      const count = message.projects || 0;
      showFormNote(message.note || `Renamed ${message.from} to ${folderName(message.to)}${count
        ? ` — ${count === 1 ? 'the project in it points' : `the ${count} projects in it point`} there now.` : '.'}`);
    },

    renameFolderFailed(message) {
      if (!els.renameDialog.open) {
        flashError(message.message);
        return;
      }
      setRenameBusy(false);
      setRenameStatus(message.message, { error: true });
      els.renameName.focus();
    },

    sharedInfoFailed(message) {
      flashError(message.message);
    },

    started(message) {
      const session = state.sessions.get(message.id);
      if (!session) return;
      Object.assign(session, { status: 'running', run: message.run, hooked: !!message.hooks });
      refresh(message.id);
      updateSummary();
    },

    launchFailed(message) {
      const session = state.sessions.get(message.id);
      if (!session) return;
      Object.assign(session, { status: 'failed', error: message.message });
      refresh(message.id);
      updateSummary();
    },

    output(message) {
      const session = state.sessions.get(message.id);
      if (!session || session.run !== message.run) return;
      session.term.write(tintOutput(session, message.data));
      noteOutput(session);
    },

    exited(message) {
      const session = state.sessions.get(message.id);
      if (session && session.run === message.run) {
        // A session the user stopped ends with a "killed" exit code; that is not a failure.
        Object.assign(session, {
          status: 'exited', exitCode: message.exitCode, stopped: session.stopRequested,
          busy: false, attention: false, waiting: false, stopRequested: false, trustWatch: null,
          historyNote: message.historyError ? `The conversation could not be saved to CLAUDE.md: ${message.historyError}`
            : message.historySaved ? 'Conversation saved to CLAUDE.md.'
            : '',
        });
        refresh(message.id);
        updateSummary();
        if (els.renameDialog.open) updateRenamePreview(); // that session no longer keeps the folder in use
      }
    },

    /**
     * The host asks for what a button here does, for Maid's API (maidctl: Dashboard.Api.cs) or a voice request;
     * answered with remoteResult (the tile used, or why not). The API's requests come `quiet`: they don't take the
     * keyboard.
     */
    remote(message) {
      const reply = (id, error = '', extra = {}) => post({ type: 'remoteResult', ref: message.ref, id: id || '', error, ...extra });
      const session = message.id ? state.sessions.get(message.id) : null;
      const quiet = !!message.quiet;
      switch (message.action) {
        case 'launchEntry': {
          const entry = findEntry(message.entry);
          if (!entry) return reply('', 'That project is no longer in the list.');
          if (state.relocating.has(entry.id)) return reply('', 'That project\'s folder is being moved.');
          const started = launchEntry(entry.id, {
            resume: !!message.resume, prompt: String(message.prompt || ''), quiet,
          });
          return reply(started ? started.id : '', started ? '' : 'Could not start it.');
        }
        case 'relaunch':
          if (!session || !findEntry(session.entry.id)) return reply('', 'That window is not open any more.');
          launch(session.id, { resume: !!message.resume, quiet });
          return reply(session.id);
        case 'show':
          if (!session) return reply('', 'That window is not open any more.');
          activate(session.id);
          return reply(session.id);
        case 'screen': {
          // What the window shows, as text: its screen, or its last `lines` lines (scrollback included).
          if (!session) return reply('', 'That window is not open any more.');
          const buffer = session.term.buffer.active;
          const wanted = Math.max(0, Math.min(Number(message.lines) || 0, 5000));
          let top = buffer.baseY;
          let bottom = buffer.baseY + session.term.rows;
          if (wanted) {
            // The last lines written (down to the cursor, or below it if something was written there), not the screen's
            // empty bottom rows.
            let last = buffer.baseY + buffer.cursorY;
            for (let y = buffer.length - 1; y > last; y--) {
              const line = buffer.getLine(y);
              if (line && line.translateToString(true).trim()) { last = y; break; }
            }
            bottom = last + 1;
            top = Math.max(0, bottom - wanted);
          }
          const lines = [];
          for (let y = top; y < bottom; y++) {
            const line = buffer.getLine(y);
            lines.push(line ? line.translateToString(true) : '');
          }
          while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
          return reply(session.id, '', { text: lines.join('\n'), cols: session.term.cols, rows: session.term.rows });
        }
        case 'stop':
          if (!session) return reply('', 'That window is not open any more.');
          stop(session.id);
          return reply(session.id);
        case 'close':
          if (session) closeTile(session.id);
          return reply(message.id);
        case 'voice': {
          // Jarvis heard "on computer, …" and the user said yes: it goes to Jarvis's own tile in All projects.
          const text = String(message.text || '').trim();
          if (!state.hub) return reply('', 'Maid has no All projects folder.');
          if (!text) return reply('', 'Nothing to send.');
          let jarvis = [...state.sessions.values()].find((candidate) => candidate.jarvis);
          if (jarvis && isLive(jarvis)) {
            // Until it has shown it is up, typing could get lost in its start-up.
            if (jarvis.status === 'running' && (jarvis.hookSeen || !jarvis.hooked)) typeLine(jarvis, text);
            else jarvis.voiceQueue.push(text);
            return reply(jarvis.id);
          }
          if (!jarvis) {
            jarvis = createSession(state.hub);
            setJarvis(jarvis, true);
          }
          launch(jarvis.id, { prompt: text, quiet: true }); // a new session gets it as its first prompt
          return reply(jarvis.id);
        }
        default:
          return reply('', 'Not understood.');
      }
    },

    /**
     * Claude Code's hooks (HookServer.cs) say what the session does, for certain: a submitted prompt means it
     * works until the turn ends (Stop), unless it only paused for background work; a question to the user,
     * a permission prompt or a failed turn means it waits.
     */
    hook(message) {
      const session = state.sessions.get(message.id);
      if (!session || session.run !== message.run || session.status !== 'running') return;
      session.hooked = true;
      const first = !session.hookSeen;
      session.hookSeen = true;
      const asking = message.kind === 'AskUserQuestion' || message.kind === 'ExitPlanMode';
      // Jarvis's tile never waits for anybody here: its answers are read out to whoever asked.
      const waits = () => { if (!session.jarvis) { setWaiting(session); raiseAttention(session); } };
      switch (message.event) {
        case 'UserPromptSubmit':
          setBusy(session, true);
          clearAttention(session);
          clearWaiting(session);
          break;
        case 'Stop':
          if (message.background) break; // it carries on by itself when its background work reports back
          setBusy(session, false);
          waits();
          break;
        case 'StopFailure':
          setBusy(session, false);
          waits();
          break;
        case 'Notification':
          if (/^(permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input)$/.test(message.kind)) waits();
          break;
        case 'PreToolUse':
          if (asking) waits();
          break;
        case 'PostToolUse':
          if (asking) setBusy(session, true);
          break;
      }
      if (first) flushVoice(session);
    },

    /** Something typed into a session from outside the page (Maid's API): as good as typing here, as far as prompts and "waiting" go. */
    remoteInput(message) {
      const session = state.sessions.get(message.id);
      if (!session) return;
      session.trustWatch = null;
      clearAttention(session);
      clearWaiting(session);
      if (message.escape) checkInterrupted(session);
    },

    paste(message) {
      const session = state.sessions.get(message.id);
      if (!session || session.status !== 'running') return;
      const viaCtrlV = session.ctrlVPending;
      session.ctrlVPending = false;
      if (message.text) session.term.paste(message.text);
      // No text on the clipboard: pass ^V through so the app can look for an image itself.
      else if (viaCtrlV) post({ type: 'input', id: session.id, data: '\x16' });
    },
  };

  if (host) {
    host.addEventListener('message', (event) => {
      const handler = event.data && handlers[event.data.type];
      if (handler) handler(event.data);
    });
    post({ type: 'ready' });
  }
  updateSummary();
})();
