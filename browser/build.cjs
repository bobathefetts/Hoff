const fs = require('node:fs');
const path = require('node:path');
const {execFileSync} = require('node:child_process');
const {packEmbedded} = require('./embedded-scripts.cjs');
const desktop = require('./desktop-contract.cjs').loadDesktopContract();

const project = __dirname;
const base = path.resolve(process.argv[2] || path.join(project, 'base'));
const work = path.join(project, '.build/assembly');
const dest = path.resolve(project, '../dist/Media Compliance Reviewer WebAssembly.html');
const read = file => fs.readFileSync(file, 'utf8');
const fromBase = file => path.join(base, file);
const fromWork = file => path.join(work, file);
function replaceOnce(text, pattern, update) {
  if (!text.includes(pattern) || text.indexOf(pattern) !== text.lastIndexOf(pattern)) {
    throw new Error('Expected one browser source marker: ' + pattern);
  }
  return text.replace(pattern, () => update);
}
for (const file of ['Media-Compliance-Reviewer-Standalone.html', 'src',
  'scripts/assemble-media-reviewer.cjs', 'scripts/install-startup-screen.cjs',
  'scripts/build-media-preview.cjs', 'scripts/build-media-database.cjs', 'scripts/test-browser-smoke.cjs',
  'vendor/wa-sqlite', 'vendor/ffmpeg-core/dist/umd', 'vendor/ffmpeg-core/COPYING']) {
  const target = fromWork(file);
  fs.mkdirSync(path.dirname(target), {recursive: true});
  fs.cpSync(fromBase(file), target, {recursive: true});
}

const workerFile = fromWork('src/media-database-worker.js');
let worker = read(workerFile);
worker = `const windowsCatalogApplicationId = ${desktop.applicationId}, windowsCatalogSchema = ${desktop.schema};\n` +
  read(path.join(project, 'windows-backup-vfs.js')) + '\n' + worker;
worker = replaceOnce(worker, 'async open({input}){\n  await boot();',
  'async open({input}){\n  await boot();if(input?.windowsBackup)return openWindowsBackup(input.windowsBackup);');
worker = replaceOnce(worker, 'databases.delete(this.id);await releaseWorkingFile(this.path);',
  'databases.delete(this.id);if(!this.windowsBackup)await releaseWorkingFile(this.path);');
fs.writeFileSync(workerFile, worker);

const appFile = fromWork('src/media-reviewer-app.js');
let app = read(appFile);
app = replaceOnce(app, "    if (!$('#inspect-dialog').open) return;\n    if (e.key === 'ArrowLeft')",
  "    if (!$('#inspect-dialog').open || e.defaultPrevented || e.target.closest('input,textarea,select,button,video,[contenteditable]')) return;\n    if (e.key === 'ArrowLeft')");
app = require('./file-picker-fallback.cjs')(app, replaceOnce);
app = replaceOnce(app, '  function updateCardDetails(node, card, occurrence) {', `
  function updateCardDetails(node, card, occurrence) {
    const folder = normalizePath(occurrence.path).split('/').slice(0,-1).join('/');
    node.__fleetRow = [occurrence.name, folder, occurrence.extension.toUpperCase(),
      formatBytes(occurrence.size), occurrence.lastModified ? safeDate(occurrence.lastModified) : '',
      card.decision.reviewer || ''];
    node.__fleetIsVideo = C.mediaKindForExtension(occurrence.extension) === 'video';`.slice(1));
app = replaceOnce(app, "button.disabled = evidenceOperationInFlight || reportExportActive);",
  "button.disabled = evidenceOperationInFlight || reportExportActive || (button.hasAttribute('data-bulk') && !selected.size));");
app = replaceOnce(app, '    await runReviewAction(keys, status);',
  "    await runReviewAction(keys, status, $('#fh-bulk-notes').value || undefined);");
app = replaceOnce(app, '  setupStickySelectionBar();', `
  mountFleetHackerDesktop({
    inspect: openInspector,
    select: selectCard,
    review: (key, status, notes) => runReviewAction([key], status, notes || undefined),
    video: async key => {
      const card = visibleCards.find(card => card.key === key);
      const source = card && previewOccurrence(card);
      if (!source || C.mediaKindForExtension(source.extension) !== 'video')
        throw new Error('Reconnect the source folder to play this video.');
      const file = await connectedFile(source);
      if (!file) throw new Error('Reconnect the source folder to play this video.');
      return file.type ? file : file.slice(0, file.size, mediaMime(source.extension));
    },
    refresh: () => renderResults(),
    chooseSource: () => { void openScanDialog(); }, // the scan dialog owns the catalog-ready check and shows scan errors
    summary: () => ({
      catalog: workspaceLoadedFromFile ? workspaceFileHandle?.name || $('#database-opening-file').textContent || 'Browser catalog'
        : 'Welcome · open or create a catalog',
      source: ws.roots[scanTargetRootId]?.label || '',
    }),
    notify: message => toast(message, true),
  }, ${JSON.stringify(desktop.themes)});
  setupStickySelectionBar();`);
fs.writeFileSync(appFile, read(path.join(project, 'desktop-shell.js')) + '\n' + app + '\n' +
  read(path.join(project, 'browser-controls.js')));

const template = fromWork('Media-Compliance-Reviewer-Standalone.html');
let html = read(template);
html = replaceOnce(html, '</head>', '<style>\n' + read(path.join(project, 'browser.css')) + '\n' +
  read(path.join(project, 'desktop-shell.css')) + '\n</style>\n</head>');
html = replaceOnce(html, '<dialog id="inspect-dialog" class="inspector">',
  read(path.join(project, 'windows-backup.html')) + '\n<dialog id="inspect-dialog" class="inspector">');
html = replaceOnce(html, '<h2 id="inspect-title">Media</h2>', '<h2 id="inspect-title">Media</h2>\n' +
  '<button id="inspect-fullscreen" type="button" title="Show the inspection window fullscreen">Full screen</button>');
html = replaceOnce(html, '<div id="inspect-zoom-controls" class="inspect-controls">',
  '<label class="inspect-video-options" for="inspect-video-rate">Speed <select id="inspect-video-rate">' +
  '<option value="1">1×</option><option value="1.25">1.25×</option><option value="1.5">1.5×</option>' +
  '<option value="2">2×</option></select></label><div id="inspect-zoom-controls" class="inspect-controls">');
const aboutHeading = '<h2>About Media Compliance Reviewer</h2>';
html = replaceOnce(html, aboutHeading, aboutHeading + '<p>Browser workspace for FleetHacker ' + desktop.version + '</p>');
const workspaceDialog = '<dialog id="workspace-dialog" class="workspace-choice">';
html = replaceOnce(html, workspaceDialog, read(path.join(project, 'desktop-shell.html')) + '\n' + workspaceDialog);
const chooser = '<button id="startup-new" disabled>Create database</button>';
html = replaceOnce(html, chooser, chooser +
  '\n<button id="windows-backup-startup" type="button">View Windows backup</button>');
const toolbar = '<button id="open-workspace-button"';
html = replaceOnce(html, toolbar,
  '<button id="windows-backup-toolbar" type="button" title="View a Windows portable backup without editing it">Windows backup</button>\n' + toolbar);
fs.writeFileSync(template, html);

const nodePath = [path.join(project, 'node_modules'), path.join(base, 'node_modules'), process.env.NODE_PATH].filter(Boolean).join(path.delimiter);
for (const script of ['build-media-database.cjs', 'assemble-media-reviewer.cjs']) {
  execFileSync(process.execPath, [path.join('scripts', script)], {
    cwd: work, env: {...process.env, NODE_PATH: nodePath}, stdio: 'inherit'
  });
}

fs.mkdirSync(path.dirname(dest), {recursive: true});
fs.writeFileSync(dest, packEmbedded(read(template)));
console.log(dest);
