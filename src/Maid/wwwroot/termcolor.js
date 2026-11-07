// The terminal colours, shared by the dashboard (app.js) and the phone app, which bundles this file:
// a group's colour has to look the same on both.
(() => {
  'use strict';

  /** How far the channels of a colour may be apart for it to still count as a grey (and take the group's colour). */
  const GREY_SPREAD = 24;

  const THEME = {
    background: '#0c0e12',
    foreground: '#d7dae0',
    cursor: '#d7dae0',
    cursorAccent: '#0c0e12',
    selectionBackground: '#34456b',
    scrollbarSliderBackground: 'rgba(139, 147, 163, .22)',
    scrollbarSliderHoverBackground: 'rgba(139, 147, 163, .4)',
    scrollbarSliderActiveBackground: 'rgba(139, 147, 163, .55)',
    black: '#1d2026', red: '#e06c75', green: '#98c379', yellow: '#e5c07b',
    blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
    brightBlack: '#5c6370', brightRed: '#f28b93', brightGreen: '#aed88f', brightYellow: '#f0d399',
    brightBlue: '#82c1f7', brightMagenta: '#d89cec', brightCyan: '#76d0da', brightWhite: '#ffffff',
  };

  const channelsOf = (color) => [1, 3, 5].map((at) => parseInt(color.slice(at, at + 2), 16));
  /** How bright the normal text is; a grey relates to the group's colour as it does to this. */
  const TEXT_LEVEL = channelsOf(THEME.foreground).reduce((sum, channel) => sum + channel, 0) / 3;

  /**
   * The shade of `color` that stands in for a grey of brightness `level` (0–255): the colour itself
   * for a grey as bright as the normal text (or brighter), darker in proportion below. Returns [r, g, b].
   */
  function shade(color, level) {
    const factor = Math.min(1, level / TEXT_LEVEL);
    return channelsOf(color).map((channel) => Math.round(channel * factor));
  }

  /** The terminal theme for a group colour: the text, and the neutral ANSI colours along with it. */
  function themeFor(color) {
    if (!color) return THEME;
    const hex = (level) => '#' + shade(color, level).map((channel) => channel.toString(16).padStart(2, '0')).join('');
    return { ...THEME, foreground: color, cursor: color, white: hex(0xb2), brightWhite: color, brightBlack: hex(0x63) };
  }

  /**
   * A theme only reaches text the program leaves in the default colour, and Claude Code paints much
   * of its own in greys it picks itself (true colour). For a session with a group colour those greys
   * are swapped, as the output streams in, for shades of that colour of the same brightness. Colours
   * that mean something (accents, diffs, syntax highlighting) and backgrounds pass through untouched.
   * `session` carries `color` ('' for none) and `tintCarry` (an escape sequence cut off at the end of
   * the previous chunk).
   */
  function tintOutput(session, data) {
    if (session.tintCarry) {
      data = session.tintCarry + data;
      session.tintCarry = '';
    }
    const color = session.color;
    if (!color) return data;
    // An escape sequence cut in two by the end of the chunk waits for its other half.
    const cut = data.match(/\x1b(?:\[[0-9;]*)?$/);
    if (cut) {
      session.tintCarry = cut[0];
      data = data.slice(0, cut.index);
    }
    return data.replace(/\x1b\[([0-9;]*)m/g, (sequence, params) => {
      if (!params.includes('38;2;')) return sequence;
      const parts = params.split(';');
      for (let i = 0; i < parts.length; i++) {
        // 38 = text colour, 48 = background, 58 = underline; each is followed by "2;r;g;b" or "5;n".
        if (parts[i] !== '38' && parts[i] !== '48' && parts[i] !== '58') continue;
        const trueColor = parts[i + 1] === '2';
        if (trueColor && parts[i] === '38') {
          const rgb = parts.slice(i + 2, i + 5).map(Number);
          const grey = rgb.length === 3 && rgb.every(Number.isFinite) && Math.max(...rgb) - Math.min(...rgb) <= GREY_SPREAD;
          if (grey) parts.splice(i + 2, 3, ...shade(color, (rgb[0] + rgb[1] + rgb[2]) / 3));
        }
        i += trueColor ? 4 : parts[i + 1] === '5' ? 2 : 0;
      }
      return `\x1b[${parts.join(';')}m`;
    });
  }

  window.MaidTerm = Object.freeze({ THEME, themeFor, tintOutput, shade });
})();
