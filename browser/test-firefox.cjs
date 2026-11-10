'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {pathToFileURL} = require('node:url');

const scratch = path.join(__dirname, '.build');
fs.mkdirSync(scratch, {recursive: true});
const fixture = fs.mkdtempSync(path.join(scratch, 'firefox-'));
const media = path.join(fixture, 'Sample media');
const downloads = path.join(fixture, 'downloads');
fs.mkdirSync(path.join(media, 'Sample user'), {recursive: true});
fs.mkdirSync(downloads);
fs.writeFileSync(path.join(media, 'Sample user', 'example.png'), Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMw7tgNAAJoAXcmSeNVAAAAAElFTkSuQmCC', 'base64'));

const driver = spawn(process.env.MEDIA_REVIEWER_GECKODRIVER || 'geckodriver',
  ['--host', '127.0.0.1', '--port', '0'], {windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']});
let output = '';
for (const stream of [driver.stdout, driver.stderr]) stream.on('data', data => { output += data; });
let startError;
driver.on('error', error => { startError = error; });

async function wait(test, description) {
  const until = Date.now() + 25000;
  while (Date.now() < until) {
    const result = await test();
    if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Timed out waiting for ' + description);
}

let origin, session;
async function request(method, target, data) {
  const response = await fetch(origin + target, {
    method, headers: {'Content-Type': 'application/json'}, body: data === undefined ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(35000)
  });
  const {value} = await response.json();
  if (!response.ok) throw new Error(value?.message || 'Firefox WebDriver error ' + response.status);
  return value;
}
const evaluate = (script, ...args) => request('POST', '/session/' + session + '/execute/sync', {script, args});
const click = id => evaluate('document.getElementById(arguments[0]).click()', id);
async function inputFile(selector, filename) {
  const elementKey = 'element-6066-11e4-a52e-4f735466cecf';
  const element = await request('POST', '/session/' + session + '/element', {using: 'css selector', value: selector});
  await request('POST', '/session/' + session + '/element/' + element[elementKey] + '/value', {text: filename});
}

async function run() {
  const port = await wait(() => {
    if (startError) throw startError;
    if (driver.exitCode !== null) throw new Error(output);
    return /Listening on 127\.0\.0\.1:(\d+)/i.exec(output)?.[1];
  }, 'geckodriver');
  origin = 'http://127.0.0.1:' + port;
  const opened = await request('POST', '/session', {capabilities: {alwaysMatch: {
    browserName: 'firefox', 'moz:firefoxOptions': {
      ...(process.env.MEDIA_REVIEWER_TEST_FIREFOX ? {binary: process.env.MEDIA_REVIEWER_TEST_FIREFOX} : {}),
      args: ['-headless'], prefs: {
        'network.proxy.type': 1, 'network.proxy.http': '127.0.0.1', 'network.proxy.http_port': 9,
        'network.proxy.ssl': '127.0.0.1', 'network.proxy.ssl_port': 9,
        'browser.download.folderList': 2, 'browser.download.dir': downloads,
        'browser.download.useDownloadDir': true, 'browser.helperApps.neverAsk.saveToDisk': 'application/x-sqlite3,application/vnd.sqlite3,application/octet-stream'
      }
    }
  }}});
  session = opened.sessionId;
  const html = path.resolve(__dirname, '../dist/Media Compliance Reviewer WebAssembly.html');
  await request('POST', '/session/' + session + '/url', {url: pathToFileURL(html).href});
  try {
    await wait(() => evaluate('return document.getElementById("workspace-dialog")?.open && !document.getElementById("startup-new").disabled'), 'offline Firefox startup');
  } catch (error) {
    throw new Error(error.message + ' (' + await evaluate('return document.getElementById("startup-status")?.textContent || document.title') + ')');
  }
  assert.equal(await evaluate('return typeof window.showSaveFilePicker'), 'undefined');
  await click('startup-new');
  try {
    await wait(() => evaluate('return !document.getElementById("workspace-dialog").open && !document.getElementById("database-opening-dialog").open && !document.getElementById("scan-button").disabled'), 'browser working database');
  } catch (error) {
    throw new Error(error.message + ' (' + await evaluate('return document.getElementById("startup-status").textContent') + ')');
  }
  await click('scan-button');
  const png = fs.readFileSync(path.join(media, 'Sample user', 'example.png')).toString('base64');
  await evaluate(`
    const input = document.getElementById('folder-input');
    if (!('webkitdirectory' in input)) throw new Error('Folder selection is unavailable');
    // WebDriver on Firefox can set regular files but cannot select a directory.
    const bytes = Uint8Array.from(atob(arguments[0]), char => char.charCodeAt(0));
    const file = new File([bytes], 'example.png', {type:'image/png'});
    Object.defineProperty(file, 'webkitRelativePath', {value:'Sample media/Sample user/example.png'});
    const selection = new DataTransfer();
    selection.items.add(file);
    input.files = selection.files;
    input.dispatchEvent(new Event('change', {bubbles:true}));`, png);
  await wait(() => evaluate('return document.getElementById("results").textContent.includes("example.png") && !document.getElementById("save-workspace").disabled'), 'folder scan');
  await evaluate('document.getElementById("scan-dialog").close()');
  await wait(() => evaluate('return document.querySelector("#results .fh-list-row")?.textContent.includes("example.png") && document.getElementById("fh-preview-image").naturalWidth > 0'), 'list row and saved side preview');
  assert.equal(await evaluate('return document.querySelector(".fh-desktop-shell").dataset.view'), 'list');
  for (const pane of ['preview','sidebar','controls']) {
    const toggle = 'fh-' + pane + '-toggle';
    await click(toggle);
    assert.equal(await evaluate('return document.getElementById(arguments[0]).getAttribute("aria-expanded")', toggle), 'false');
    await click(toggle);
    assert.equal(await evaluate('return document.getElementById(arguments[0]).getAttribute("aria-expanded")', toggle), 'true');
  }
  const view = name => evaluate('document.querySelector("[data-fh-view=" + arguments[0] + "]").click()', name);
  await view('gallery');
  assert.equal(await evaluate('return document.querySelector(".fh-desktop-shell").dataset.view'), 'gallery');
  await view('quick');
  await wait(() => evaluate('return document.getElementById("fh-quick-name").textContent === "example.png"'), 'Quick Review item');
  // Dragging a zoomed picture must pan it, never review it.
  await click('fh-zoom-in');
  await click('fh-zoom-in');
  const elementKey = 'element-6066-11e4-a52e-4f735466cecf';
  const stage = await request('POST', '/session/' + session + '/element', {using: 'css selector', value: '#fh-quick-stage'});
  const dragDown = () => request('POST', '/session/' + session + '/actions', {actions: [{
    type: 'pointer', id: 'mouse', parameters: {pointerType: 'mouse'}, actions: [
      {type: 'pointerMove', duration: 0, origin: {[elementKey]: stage[elementKey]}, x: 0, y: -40},
      {type: 'pointerDown', button: 0},
      {type: 'pointerMove', duration: 50, origin: 'pointer', x: 0, y: 120},
      {type: 'pointerUp', button: 0}]}]});
  await dragDown();
  await new Promise(resolve => setTimeout(resolve, 400));
  assert.deepEqual(await evaluate(`return [
    document.getElementById('count-COMPLIANT').textContent, document.getElementById('count-NON_COMPLIANT').textContent,
    document.getElementById('fh-quick-good').disabled,
    /^translate\\(0px, ?[1-9]/.test(document.getElementById('fh-quick-image').style.transform)]`),
    ['0', '0', false, true], 'a drag on a zoomed Quick Review picture must pan without recording a decision');
  await click('fh-zoom-reset');
  assert.match(await evaluate('return document.getElementById("fh-quick-image").style.transform'), /^translate\(0px(, ?0px)?\) rotate\(0deg\) scale\(1\)$/);
  // Two fingers on the unzoomed picture (a pinch or two-finger tap) must not be measured as one swipe.
  // Headless Firefox delivers no events for WebDriver touch actions, so the two contacts are dispatched directly.
  await evaluate(`
    const stage = document.getElementById('fh-quick-stage'), box = stage.getBoundingClientRect();
    const x = box.left + box.width / 2, middle = box.top + box.height / 2;
    const send = (type, pointerId, isPrimary, y) => stage.dispatchEvent(new PointerEvent(type,
      {pointerId, isPrimary, pointerType:'touch', button:0, clientX:x, clientY:y, bubbles:true}));
    send('pointerdown', 21, true, middle - 70);
    send('pointerdown', 22, false, middle + 70);
    send('pointerup', 21, true, middle - 70);
    send('pointerup', 22, false, middle + 70);`);
  await new Promise(resolve => setTimeout(resolve, 400));
  assert.deepEqual(await evaluate(`return [
    document.getElementById('count-COMPLIANT').textContent, document.getElementById('count-NON_COMPLIANT').textContent,
    document.getElementById('fh-quick-good').disabled]`), ['0', '0', false], 'two fingers on Quick Review must not record a decision');
  await evaluate(`
    document.getElementById('fh-bulk-notes').value = 'Reviewed via Quick Review';
    document.getElementById('fh-quick').dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowUp', bubbles:true}));`);
  await wait(() => evaluate('return document.getElementById("count-COMPLIANT").textContent === "1"'), 'Quick Review decision');
  await view('list');
  await evaluate('document.querySelector("[data-bucket=COMPLIANT]").click()');
  await wait(() => evaluate('return document.getElementById("results").textContent.includes("example.png")'), 'reviewed item in Compliant');
  await request('POST', '/session/' + session + '/window/rect', {width: 1030, height: 800});
  assert.equal(await evaluate(`
    const rect = selector => document.querySelector(selector).getBoundingClientRect();
    return rect('#fh-sidebar').top === rect('.fh-desktop-shell').top &&
      rect('#fh-preview').bottom <= rect('#fh-pagination').top &&
      rect('#fh-pagination').bottom <= rect('.fh-status').top;`), true, 'panes and pagination stay within their rows');
  assert.equal(await evaluate(`
    const review = document.querySelector('.fh-review');
    const toggle = document.getElementById('fh-controls-toggle');
    const before = review.getBoundingClientRect().height;
    toggle.click();
    const expanded = review.getBoundingClientRect().height > before;
    toggle.click();
    return expanded;`), true, 'collapsing controls gives the media workspace more space');
  assert.equal(await evaluate(`
    const preview = document.getElementById('fh-preview');
    const before = preview.getBoundingClientRect().width;
    document.getElementById('fh-preview-size').dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowLeft', bubbles:true}));
    return preview.getBoundingClientRect().width > before;`), true, 'preview separator supports keyboard resizing');
  await evaluate('document.getElementById("fh-preview-size").dispatchEvent(new KeyboardEvent("keydown", {key:"ArrowRight", bubbles:true}))');
  if (process.env.MEDIA_REVIEWER_TEST_SCREENSHOT) {
    const screenshot = await request('GET', '/session/' + session + '/screenshot');
    fs.writeFileSync(process.env.MEDIA_REVIEWER_TEST_SCREENSHOT, Buffer.from(screenshot, 'base64'));
  }
  await click('save-workspace');
  const portable = await wait(() => {
    const file = fs.readdirSync(downloads).find(name => name.endsWith('.sqlite'));
    if (!file || fs.readdirSync(downloads).some(name => name.endsWith('.part'))) return null;
    const location = path.join(downloads, file);
    return fs.statSync(location).size > 100 ? location : null;
  }, 'SQLite download');
  assert.equal(fs.readFileSync(portable).subarray(0, 16).toString(), 'SQLite format 3\0');
  await inputFile('#open-workspace', portable);
  await wait(() => evaluate('return !document.getElementById("workspace-dialog").open && !document.getElementById("database-opening-dialog").open'), 'reopened portable catalog');
  await evaluate('document.querySelector("[data-bucket=COMPLIANT]").click()');
  await wait(() => evaluate('return document.getElementById("results").textContent.includes("example.png")'), 'persisted Compliant decision');
  await click('fh-preview-details');
  await wait(() => evaluate('return document.getElementById("inspect-dialog").open && document.getElementById("review-notes").value === "Reviewed via Quick Review"'), 'persisted decision note');
  await evaluate('document.getElementById("inspect-dialog").close()');

  if (process.argv[2]) {
    await click('windows-backup-toolbar');
    await inputFile('#windows-backup-file', path.resolve(process.argv[2]));
    await wait(() => evaluate('return document.getElementById("windows-backup-status").textContent.startsWith("Read-only")'), 'Windows portable backup');
    assert.ok(await evaluate('return document.querySelectorAll("#windows-backup-cards > li").length > 0'));
  }
  console.log('Offline Firefox passed: folder scan, workspace views and panes, Quick Review decision, SQLite download and reopen' + (process.argv[2] ? ', and Windows backup viewing.' : '.'));
}
run().catch(error => { console.error(error.message); process.exitCode = 1; }).finally(async () => {
  if (session) await request('DELETE', '/session/' + session).catch(() => {});
  driver.kill();
});
