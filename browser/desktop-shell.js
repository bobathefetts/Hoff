function mountFleetHackerDesktop(api, themes) {
  const byId = id => document.getElementById(id);
  const oldLayout = document.querySelector('.layout');
  const oldHeader = document.querySelector('.app-head');
  const shell = byId('fh-desktop-template').content.firstElementChild.cloneNode(true);
  oldLayout.before(shell);
  document.body.classList.add('fh-desktop');
  document.documentElement.classList.add('fh-browser-root');
  const move = (element, destination) => byId(destination).append(element);
  const moveId = (id, destination) => move(byId(id), destination);
  const make = (tag, text, className) => {
    const node = document.createElement(tag);
    node.textContent = text;
    if (className) node.className = className;
    return node;
  };
  const expander = (label, className) => {
    const details = make('details', '', className);
    details.append(make('summary', label));
    return details;
  };
  const text = (id, value) => {
    const node = byId(id);
    if (node.textContent !== value) node.textContent = value;
  };
  const nameButton = (id, name) => { byId(id).textContent = name; };

  move(oldHeader.querySelector('.head-actions'), 'fh-catalog-tools');
  for (const id of ['export-report', 'maintenance-button', 'lock-vault']) moveId(id, 'fh-catalog-tools');
  const filters = oldLayout.querySelector('[aria-labelledby="show-results-heading"]');
  for (const child of [...filters.children].slice(2)) move(child, 'fh-filters');
  moveId('result-summary', 'fh-workspace-state');
  moveId('workspace-state', 'fh-workspace-state');
  moveId('operation-status', 'fh-operation');
  moveId('cancel-operation', 'fh-operation');
  move(oldLayout.querySelector('.dashboard'), 'fh-queues');
  move(byId('thumb-size').parentElement, 'fh-card-size');
  move(byId('thumb-fit').parentElement, 'fh-filters');
  moveId('roots-list', 'fh-filters');
  moveId('scan-button', 'fh-start');
  moveId('resume-scan', 'fh-resume');
  moveId('live-cancel-scan', 'fh-pause');
  moveId('resume-scan-banner', 'fh-scan-banners');
  moveId('live-scan-banner', 'fh-scan-banners');
  moveId('search', 'fh-search-field');
  moveId('clear-filters', 'fh-reset');
  moveId('results', 'fh-results-slot');
  move(oldLayout.querySelector('.pagination'), 'fh-pagination');

  const actions = byId('selectionbar');
  actions.querySelector('.spacer')?.remove();
  const more = expander('More review actions', 'fh-expander');
  const morePanel = make('div', '', 'fh-more-actions');
  morePanel.append(actions.querySelector('.tiny'), actions.querySelector('[data-bulk="EVIDENCE"]'),
    byId('export-evidence'), byId('purge-selected'));
  more.append(morePanel);
  const notes = expander('Reviewer and notes', 'fh-expander');
  const notesPanel = make('div', '', 'fh-notes-fields');
  const bulkNotes = make('input', '');
  bulkNotes.type = 'text';
  bulkNotes.id = 'fh-bulk-notes';
  bulkNotes.placeholder = 'Decision notes';
  bulkNotes.maxLength = 16384;
  const noteLabel = make('label', 'Note');
  noteLabel.htmlFor = bulkNotes.id;
  notesPanel.append(byId('reviewer').closest('.field'), noteLabel, bulkNotes);
  notes.append(notesPanel);
  actions.append(actions.querySelector('[data-bulk="COMPLIANT"]'), actions.querySelector('[data-bulk="NON_COMPLIANT"]'),
    actions.querySelector('[data-bulk="TO_REVIEW"]'), byId('select-visible'), byId('clear-selection'), notes, more);
  move(actions, 'fh-actions');
  oldHeader.remove();
  oldLayout.remove();

  nameButton('scan-button', '✦ Start scan');
  nameButton('resume-scan', '▶ Resume');
  nameButton('live-cancel-scan', 'Pause scan');
  nameButton('clear-filters', 'Show all');
  nameButton('select-visible', 'Select page');
  nameButton('clear-selection', 'Clear');
  nameButton('prev-page', '← Earlier');
  nameButton('next-page', 'More →');
  nameButton('page-jump', 'Jump');
  byId('fh-pagination').querySelector('label[for="items-per-page"]').firstChild.textContent = 'Show per page ';
  const pageNumber = byId('page-number');
  pageNumber.setAttribute('aria-label', 'Go to page');
  pageNumber.parentElement.firstChild.textContent = '';
  actions.querySelector('[data-bulk="COMPLIANT"]').textContent = '♧ Looks good';
  actions.querySelector('[data-bulk="NON_COMPLIANT"]').textContent = '⚑ Flag item';
  actions.querySelector('[data-bulk="TO_REVIEW"]').textContent = '↩ Back to review';
  actions.querySelector('[data-bulk="COMPLIANT"]').classList.add('fh-good');
  actions.querySelector('[data-bulk="NON_COMPLIANT"]').classList.add('fh-bad');
  byId('search').placeholder = '';
  const buckets = [...shell.querySelectorAll('.bucket')];
  const bucketIcons = {TO_REVIEW:'⇩', COMPLIANT:'♧', NON_COMPLIANT:'⚑', EVIDENCE:'♙'};
  for (const bucket of buckets) {
    const marker = bucket.querySelector('.dot');
    if (marker) marker.replaceWith(make('span', bucketIcons[bucket.dataset.bucket], 'fh-bucket-icon'));
    bucket.title = 'Show ' + bucket.querySelector('span').textContent.trim();
  }

  const settings = byId('fh-settings');
  const compact = byId('fh-compact-queue');
  for (const bucket of buckets) compact.add(new Option('', bucket.dataset.bucket));
  compact.addEventListener('change', () => buckets.find(bucket => bucket.dataset.bucket === compact.value)?.click());
  settings.addEventListener('click', event => {
    if (event.target.closest('.fh-settings-menu button')) settings.open = false;
  });
  document.addEventListener('pointerdown', event => {
    if (settings.open && !settings.contains(event.target)) settings.open = false;
  });
  settings.addEventListener('keydown', event => { if (event.key === 'Escape') settings.open = false; });
  byId('fh-catalog-choice').addEventListener('click', () => {
    const dialog = byId('workspace-dialog');
    if (!dialog.open) dialog.showModal();
  });

  const storageKey = 'fleethacker-browser-appearance';
  const theme = byId('fh-theme');
  const names = ['bg','panel','text','muted','inset','line','sidebar','sidebar-text','sidebar-active',
    'accent','accent2','primary-text','good-fill','good-text','bad-fill','bad-text','selected','warn-fill','warn-text'];
  for (const [id, definition] of Object.entries(themes)) theme.add(new Option(definition.label, id));
  function applyTheme(id) {
    const definition = themes[id] || themes['warm-light'];
    const root = document.documentElement;
    root.style.colorScheme = definition.dark ? 'dark' : 'light';
    root.classList.toggle('fh-dark', definition.dark);
    definition.colors.forEach((color, index) => root.style.setProperty('--fh-' + names[index], color));
  }
  try { theme.value = localStorage.getItem(storageKey) || 'warm-light'; } catch {}
  if (!theme.value) theme.value = 'warm-light';
  applyTheme(theme.value);
  theme.addEventListener('change', () => {
    applyTheme(theme.value);
    try { localStorage.setItem(storageKey, theme.value); } catch {}
  });

  function bindToggle(name, label, openIcon, closeIcon) {
    const button = byId('fh-' + name + '-toggle');
    const update = () => {
      const expanded = !shell.classList.contains('fh-' + name + '-collapsed');
      button.setAttribute('aria-expanded', String(expanded));
      const description = (expanded ? 'Collapse ' : 'Expand ') + label;
      button.setAttribute('aria-label', description);
      button.title = description;
      button.textContent = expanded ? openIcon : closeIcon;
    };
    button.addEventListener('click', () => { shell.classList.toggle('fh-' + name + '-collapsed'); update(); });
    update();
  }
  bindToggle('sidebar', 'sidebar', '‹', '›');
  bindToggle('controls', 'top controls', '⌃', '⌄');
  bindToggle('preview', 'preview pane', '›', '‹');

  const separator = byId('fh-preview-size');
  const reviewArea = separator.closest('.fh-review');
  function resize(width) {
    const max = Math.max(170, reviewArea.clientWidth - 270);
    const actual = Math.round(Math.min(max, Math.max(170, width)));
    shell.style.setProperty('--fh-preview-width', actual + 'px');
    separator.setAttribute('aria-valuemin', '170');
    separator.setAttribute('aria-valuemax', String(max));
    separator.setAttribute('aria-valuenow', String(actual));
  }
  let resizeDrag;
  separator.addEventListener('pointerdown', event => {
    if (event.button !== 0) return;
    resizeDrag = {x:event.clientX, width:byId('fh-preview').getBoundingClientRect().width};
    separator.setPointerCapture(event.pointerId);
  });
  separator.addEventListener('pointermove', event => {
    if (resizeDrag) resize(resizeDrag.width + resizeDrag.x - event.clientX);
  });
  for (const name of ['pointerup','pointercancel','lostpointercapture']) separator.addEventListener(name, () => { resizeDrag = null; });
  separator.addEventListener('keydown', event => {
    if (!['ArrowLeft','ArrowRight'].includes(event.key)) return;
    event.preventDefault();
    resize(byId('fh-preview').getBoundingClientRect().width + (event.key === 'ArrowLeft' ? 24 : -24));
  });

  byId('fh-choose-source').title = 'Choose a folder and start scanning';
  byId('fh-choose-source').addEventListener('click', api.chooseSource);
  byId('fh-search-button').addEventListener('click', api.refresh);
  byId('fh-refresh').addEventListener('click', api.refresh);
  byId('fh-source').addEventListener('keydown', event => { if (event.key === 'Enter' && !byId('fh-choose-source').disabled) api.chooseSource(); });

  const results = byId('results');
  const quick = byId('fh-quick');
  const stage = byId('fh-quick-stage');
  const video = byId('fh-quick-video');
  const play = byId('fh-quick-play');
  const rate = byId('fh-quick-rate');
  const rateField = byId('fh-quick-rate-field');
  let videoRequest = 0, videoUrl = '';
  let focusedKey = null, position = 0, frame = 0, scale = 1, rotation = 0, panX = 0, panY = 0, drag, pending = false;
  let rows = [];
  function resetVideo() {
    ++videoRequest;
    if (videoUrl) {
      video.onloadeddata = video.onerror = null;
      video.pause();
      video.removeAttribute('src');
      video.load();
      URL.revokeObjectURL(videoUrl);
      videoUrl = '';
    }
    video.hidden = rateField.hidden = true;
    play.hidden = false;
    play.disabled = false;
    play.textContent = 'Open video';
  }
  function setFocus(row) {
    if (!row) return;
    position = rows.indexOf(row);
    refresh(row.dataset.key);
  }
  function setView(view) {
    if (document.fullscreenElement === quick && view !== 'quick') document.exitFullscreen().catch(() => {});
    if (view !== 'quick' && shell.dataset.view === 'quick') resetVideo();
    shell.dataset.view = view;
    quick.hidden = view !== 'quick';
    for (const button of shell.querySelectorAll('[data-fh-view]'))
      button.setAttribute('aria-pressed', String(button.dataset.fhView === view));
    refresh();
    if (view === 'quick') quick.focus();
  }
  for (const button of shell.querySelectorAll('[data-fh-view]'))
    button.addEventListener('click', () => setView(button.dataset.fhView));

  function renderRow(card) {
    let row = card.querySelector('.fh-list-row');
    if (!row) {
      row = make('div', '', 'fh-list-row');
      row.tabIndex = 0;
      for (let i = 0; i < 6; i++) row.append(make('span', ''));
      card.append(row);
    }
    const cells = card.__fleetRow;
    if (cells) cells.forEach((value, index) => {
      if (row.children[index].textContent !== value) row.children[index].textContent = value;
    });
    return row;
  }
  function updatePicture(prefix, card) {
    const image = byId(prefix + '-image');
    const original = card?.querySelector('.thumb img');
    const missing = byId(prefix + '-empty');
    const url = original?.getAttribute('src') || '';
    if (url) {
      if (image.getAttribute('src') !== url) image.src = url;
      image.alt = original.alt;
    } else image.removeAttribute('src');
    image.hidden = !url;
    missing.hidden = Boolean(url);
    missing.textContent = card ? 'Saved preview unavailable' : prefix === 'fh-quick' ? 'No items to review' : 'Select an item to preview';
  }
  function refresh(requestedKey = focusedKey) {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    rows = [...results.querySelectorAll('.card')];
    const previous = focusedKey;
    const card = rows.find(row => row.dataset.key === requestedKey) ||
      rows[Math.max(0, Math.min(position, rows.length - 1))];
    focusedKey = card?.dataset.key || null;
    position = card ? rows.indexOf(card) : 0;
    if (focusedKey !== previous) { scale = 1; rotation = 0; panX = panY = 0; resetVideo(); }
    for (const row of rows) {
      renderRow(row);
      row.classList.toggle('fh-focused', row === card);
    }

    const name = card?.querySelector('.filename')?.textContent || '';
    const location = card?.querySelector('.path')?.dataset.fullPath || '';
    const info = card?.querySelector('.card-locations')?.textContent || '';
    text('fh-preview-name', name);
    text('fh-preview-path', location);
    text('fh-preview-info', info);
    byId('fh-preview-details').disabled = byId('fh-preview-inspect').disabled = !card;
    if (!shell.classList.contains('fh-preview-collapsed') && shell.dataset.view !== 'quick') updatePicture('fh-preview', card);
    if (shell.dataset.view === 'quick') {
      text('fh-quick-position', card ? 'Item ' + (position + 1) + ' of ' + rows.length + ' on this page' : '');
      text('fh-quick-name', name || 'No items');
      text('fh-quick-path', location);
      if (video.hidden) updatePicture('fh-quick', card);
      play.hidden = !card?.__fleetIsVideo || !video.hidden;
      byId('fh-quick-image').style.transform = quickTransform();
      stage.classList.toggle('fh-zoomed', scale > 1.01);
      byId('fh-quick-prev').disabled = !card || position === 0;
      byId('fh-quick-next').disabled = !card || position === rows.length - 1;
      byId('fh-quick-good').disabled = byId('fh-quick-bad').disabled = pending || !card;
      byId('fh-quick-inspect').disabled = !card;
    }
    for (const [index, bucket] of buckets.entries()) {
      const count = bucket.querySelector('.count').textContent;
      const label = bucket.querySelector(':scope > span').textContent.trim();
      compact.options[index].textContent = label + ' (' + count + ')';
      if (bucket.classList.contains('active')) compact.value = bucket.dataset.bucket;
    }
    const summary = api.summary();
    text('fh-catalog', summary.catalog);
    byId('fh-catalog').title = summary.catalog;
    byId('fh-source').value = summary.source;
    byId('fh-choose-source').disabled = byId('scan-button').disabled;
    byId('resume-scan').disabled = byId('resume-scan-banner').classList.contains('hidden') || byId('scan-button').disabled;
    byId('live-cancel-scan').disabled = byId('live-scan-banner').classList.contains('hidden');
  }
  function schedule() { if (!frame) frame = requestAnimationFrame(() => refresh()); }
  const observer = new MutationObserver(schedule);
  for (const target of [results, byId('fh-queues'), byId('workspace-state'), byId('live-scan-banner'), byId('resume-scan-banner'), byId('scan-button')])
    observer.observe(target, {subtree:true, childList:true, characterData:true, attributes:true, attributeFilter:['class','disabled','src','data-full-path']});
  byId('fh-preview-toggle').addEventListener('click', schedule);

  results.addEventListener('click', event => {
    const card = event.target.closest('.card');
    if (!card) return;
    setFocus(card);
    if (shell.dataset.view === 'list' && event.target.closest('.fh-list-row') && event.detail < 2) api.select(focusedKey, event);
  });
  results.addEventListener('dblclick', event => {
    if (event.target.closest('.fh-list-row')) api.inspect(event.target.closest('.card').dataset.key);
  });
  results.addEventListener('keydown', event => {
    const row = event.target.closest('.fh-list-row');
    if (!row || !['Enter',' ','ArrowDown','ArrowUp'].includes(event.key)) return;
    event.preventDefault();
    const card = row.closest('.card');
    setFocus(card);
    if (event.key === 'Enter') api.inspect(focusedKey);
    else if (event.key === ' ') api.select(focusedKey, event);
    else {
      const next = rows[position + (event.key === 'ArrowUp' ? -1 : 1)];
      if (next) { setFocus(next); next.querySelector('.fh-list-row').focus(); }
    }
  });
  for (const id of ['fh-preview-details','fh-preview-inspect','fh-quick-inspect'])
    byId(id).addEventListener('click', () => { if (focusedKey) api.inspect(focusedKey); });
  byId('fh-preview-image').addEventListener('dblclick', () => { if (focusedKey) api.inspect(focusedKey); });

  const previous = () => setFocus(rows[position - 1]);
  const next = () => setFocus(rows[position + 1]);
  async function mark(status) {
    if (pending || !focusedKey) return;
    pending = true;
    refresh();
    try { await api.review(focusedKey, status, bulkNotes.value); }
    finally { pending = false; refresh(); }
  }
  byId('fh-quick-prev').addEventListener('click', previous);
  byId('fh-quick-next').addEventListener('click', next);
  byId('fh-quick-good').addEventListener('click', () => mark('COMPLIANT'));
  byId('fh-quick-bad').addEventListener('click', () => mark('NON_COMPLIANT'));
  rate.addEventListener('change', () => { video.playbackRate = Number(rate.value); });
  play.addEventListener('click', async () => {
    resetVideo();
    const request = videoRequest;
    play.disabled = true;
    play.textContent = 'Opening video…';
    function fail(message) {
      if (request !== videoRequest) return;
      resetVideo();
      refresh();
      api.notify(message);
    }
    try {
      const file = await api.video(focusedKey);
      if (request !== videoRequest) return;
      videoUrl = URL.createObjectURL(file);
      video.onerror = () => fail('This browser cannot play the original video.');
      video.onloadeddata = () => {
        if (request !== videoRequest) return;
        byId('fh-quick-image').hidden = byId('fh-quick-empty').hidden = true;
        video.hidden = rateField.hidden = false;
        play.hidden = true;
        video.playbackRate = Number(rate.value);
        video.focus();
      };
      video.src = videoUrl;
      video.load();
    } catch (error) {
      fail(error.message || 'The original video could not be opened.');
    }
  });
  function quickTransform() {
    return 'translate(' + panX + 'px,' + panY + 'px) rotate(' + rotation + 'deg) scale(' + scale + ')';
  }
  // Keep part of a zoomed picture inside the stage; at normal size there is nothing to pan.
  function setPan(x, y) {
    const limitX = Math.max(0, (scale - 1) * stage.clientWidth / 2);
    const limitY = Math.max(0, (scale - 1) * stage.clientHeight / 2);
    panX = Math.max(-limitX, Math.min(limitX, x));
    panY = Math.max(-limitY, Math.min(limitY, y));
    byId('fh-quick-image').style.transform = quickTransform();
  }
  function zoom(delta) {
    scale = Math.max(1, Math.min(6, scale * delta));
    if (scale <= 1.01) scale = 1;
    setPan(panX, panY);
    refresh();
  }
  byId('fh-zoom-out').addEventListener('click', () => zoom(1 / 1.25));
  byId('fh-zoom-in').addEventListener('click', () => zoom(1.25));
  byId('fh-zoom-reset').addEventListener('click', () => { scale = 1; rotation = 0; panX = panY = 0; refresh(); });
  byId('fh-rotate').addEventListener('click', () => { rotation = (rotation + 90) % 360; refresh(); });
  stage.addEventListener('wheel', event => {
    if (!video.hidden) return;
    event.preventDefault();
    zoom(event.deltaY < 0 ? 1.25 : 1 / 1.25);
  }, {passive:false});
  const fullscreen = async () => {
    try {
      if (document.fullscreenElement === quick) await document.exitFullscreen();
      else await quick.requestFullscreen();
    } catch { api.notify('Fullscreen is unavailable in this browser.'); }
  };
  byId('fh-quick-full').disabled = !document.fullscreenEnabled;
  byId('fh-quick-full').addEventListener('click', fullscreen);
  document.addEventListener('fullscreenchange', () => text('fh-quick-full',
    document.fullscreenElement === quick ? 'Exit full screen' : 'Full screen'));
  const keys = {ArrowLeft:previous, ArrowRight:next, ArrowUp:() => mark('COMPLIANT'),
    ArrowDown:() => mark('NON_COMPLIANT'), F11:fullscreen, f:fullscreen};
  quick.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey ||
        event.target.closest('input,select,textarea,video') || !keys[event.key]) return;
    event.preventDefault();
    keys[event.key]();
  });
  // As in the Windows app, dragging a zoomed picture pans it and never reviews it. Only a drag that
  // starts and ends on the same unzoomed item counts as a swipe, and only one pointer may take part:
  // a second finger (a pinch or two-finger tap) cancels the gesture instead of being measured against the first.
  stage.addEventListener('pointerdown', event => {
    if (!event.isPrimary) { if (drag) drag.cancelled = true; return; }
    if (event.button !== 0 || event.target.closest('video')) return;
    drag = {id:event.pointerId, x:event.clientX, y:event.clientY, panX, panY, pan:scale > 1.01, key:focusedKey, cancelled:false};
    try { stage.setPointerCapture(event.pointerId); } catch { /* The pointer already ended. */ }
  });
  stage.addEventListener('pointermove', event => {
    if (drag?.pan && !drag.cancelled && event.pointerId === drag.id)
      setPan(drag.panX + event.clientX - drag.x, drag.panY + event.clientY - drag.y);
  });
  stage.addEventListener('pointerup', event => {
    if (!drag || event.pointerId !== drag.id) return;
    const x = event.clientX - drag.x, y = event.clientY - drag.y;
    const swipe = !drag.cancelled && !drag.pan && scale <= 1.01 && drag.key === focusedKey;
    drag = null;
    if (!swipe || Math.max(Math.abs(x), Math.abs(y)) < 48) return;
    if (Math.abs(x) > Math.abs(y)) keys[x < 0 ? 'ArrowLeft' : 'ArrowRight']();
    else keys[y < 0 ? 'ArrowUp' : 'ArrowDown']();
  });
  for (const name of ['pointercancel', 'lostpointercapture'])
    stage.addEventListener(name, event => { if (drag && event.pointerId === drag.id) drag = null; });
  setView('list');
}
