// Drives the Maid UI through the WebView2 remote-debugging port (Chrome DevTools Protocol), so the
// dashboard can be tested end to end without touching the screen. Needs Node 22+ (global WebSocket).
//
// Start Maid with the port enabled and an isolated profile (nothing of this is compiled into the app):
//   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"
//   $env:MAID_DATA_DIR = "<some scratch folder>"        # keeps the real %APPDATA%\Maid untouched
//   Start-Process src\Maid\bin\Debug\net9.0-windows\Maid.exe
// with "settings": { "runAsAdmin": false } in that folder's state.json: an elevated WebView2 ignores
// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, so an elevated Maid never opens the port.
//
// Then:
//   node tools/cdp.js reload                     reload the page with the test hook (below) installed
//   node tools/cdp.js eval "<js expression>"     evaluate in the page, print the result (promises are awaited)
//   node tools/cdp.js shot <file.png>            screenshot of the page
//   node tools/cdp.js clip <x> <y> <w> <h> <scale> <file.png>
//   node tools/cdp.js type "<text>"              insert text into the focused element (e.g. a terminal)
//   node tools/cdp.js key <name> [modifiers]     press a key; modifiers bitmask: 1=Alt 2=Ctrl 4=Meta 8=Shift
//   node tools/cdp.js mouse <x> <y>              move the mouse there (hover)
//   node tools/cdp.js click <x> <y>              left click there
//   node tools/cdp.js drag <x1> <y1> <x2> <y2>   press at (x1, y1), move in steps, release at (x2, y2)
//
// After `reload`, every xterm.js instance is collected in window.__terms (creation order) and
// window.__dump(i) returns the text of terminal i, e.g.  node tools/cdp.js eval "__dump(0)"
// Tip: app.js/app.css can be copied straight into the build output's wwwroot and picked up with `reload`;
// running sessions survive the reload and the UI re-attaches to them.
const fs = require('fs');
// CDP_PORT / CDP_MATCH pick another target.
const PORT = process.env.CDP_PORT || 9333;

const HOOK = `
  window.__terms = [];
  { let T; Object.defineProperty(window, 'Terminal', { configurable: true, get() { return T; },
      set(v) { T = class extends v { constructor(o) { super(o); window.__terms.push(this); } }; } }); }
  window.__dump = (i) => {
    const buffer = __terms[i].buffer.active; const lines = [];
    for (let y = 0; y < buffer.length; y++) lines.push(buffer.getLine(y).translateToString(true));
    return lines.join('\\n').trimEnd();
  };`;

const KEYS = {
  Enter: { key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' },
  Tab: { key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 },
  Escape: { key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 },
  F11: { key: 'F11', code: 'F11', windowsVirtualKeyCode: 122 },
  ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40 },
  ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', windowsVirtualKeyCode: 38 },
  Equal: { key: '=', code: 'Equal', windowsVirtualKeyCode: 187 },
  Minus: { key: '-', code: 'Minus', windowsVirtualKeyCode: 189 },
};
for (let d = 0; d <= 9; d++) KEYS['Digit' + d] = { key: String(d), code: 'Digit' + d, windowsVirtualKeyCode: 48 + d };

async function connect() {
  const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
  const match = process.env.CDP_MATCH || 'maid.example';
  const page = targets.find((target) => target.type === 'page' && target.url.includes(match));
  if (!page) throw new Error('Maid page not found among: ' + JSON.stringify(targets.map((target) => target.url)));
  const socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let nextId = 1;
  const pending = new Map();
  socket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
    else waiter.resolve(message.result);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
  return { send, close: () => socket.close() };
}

async function saveScreenshot(cdp, file, clip) {
  const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', ...(clip ? { clip } : {}) });
  fs.writeFileSync(file, Buffer.from(data, 'base64'));
  console.log('saved ' + file);
}

(async () => {
  const [command, ...args] = process.argv.slice(2);
  const cdp = await connect();
  try {
    switch (command) {
      case 'eval': {
        const result = await cdp.send('Runtime.evaluate', { expression: args[0], awaitPromise: true, returnByValue: true });
        if (result.exceptionDetails) {
          console.error('EXCEPTION', JSON.stringify(result.exceptionDetails.exception || result.exceptionDetails, null, 1));
          process.exitCode = 1;
        } else {
          const value = result.result.value;
          console.log(typeof value === 'string' ? value : JSON.stringify(value, null, 1));
        }
        break;
      }
      case 'reload':
        // Installed before any page script runs, so terminals created during start-up are captured too.
        await cdp.send('Page.enable');
        await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: HOOK });
        await cdp.send('Page.reload', { ignoreCache: true });
        await new Promise((resolve) => setTimeout(resolve, 2000));
        console.log('reloaded with hook');
        break;
      case 'shot':
        await saveScreenshot(cdp, args[0]);
        break;
      case 'clip': {
        const [x, y, width, height, scale, file] = args;
        await saveScreenshot(cdp, file, { x: +x, y: +y, width: +width, height: +height, scale: +scale });
        break;
      }
      case 'type':
        await cdp.send('Input.insertText', { text: args[0] });
        console.log('typed');
        break;
      case 'mouse': {
        const [x, y] = args.map(Number);
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        console.log(`mouse at ${x},${y}`);
        break;
      }
      case 'click': {
        const [x, y] = args.map(Number);
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
        console.log(`clicked ${x},${y}`);
        break;
      }
      case 'drag': {
        const [x1, y1, x2, y2] = args.map(Number);
        const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x1, y: y1 });
        await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: x1, y: y1, button: 'left', clickCount: 1 });
        const steps = 12;
        for (let step = 1; step <= steps; step++) {
          const x = x1 + (x2 - x1) * step / steps;
          const y = y1 + (y2 - y1) * step / steps;
          await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'left', buttons: 1 });
          await pause(30);
        }
        await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x2, y: y2, button: 'left', clickCount: 1 });
        console.log(`dragged ${x1},${y1} -> ${x2},${y2}`);
        break;
      }
      case 'key': {
        const name = args[0];
        const modifiers = Number(args[1] || 0);
        const { text, ...key } = KEYS[name] || {
          key: name, code: 'Key' + name.toUpperCase(), windowsVirtualKeyCode: name.toUpperCase().charCodeAt(0), text: name,
        };
        const printable = text && !modifiers;
        await cdp.send('Input.dispatchKeyEvent', { type: printable ? 'keyDown' : 'rawKeyDown', modifiers, ...key, ...(printable ? { text } : {}) });
        await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', modifiers, ...key });
        console.log(`key ${name} modifiers=${modifiers}`);
        break;
      }
      default:
        throw new Error('Unknown command: ' + command);
    }
  } finally {
    cdp.close();
  }
})().catch((error) => { console.error(String(error)); process.exit(1); });
