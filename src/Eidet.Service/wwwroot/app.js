// Eidet Memory Explorer — Single Page Application
(function () {
  'use strict';

  const API = '';
  let currentRepo = '';
  let repoPathMap = {}; // normalized repoId → original filesystem path
  let browseSkip = 0;
  const browseTake = 30;
  let currentDetailEntry = null; // currently displayed memory entry
  let enrichmentAvailable = null; // cached enrichment availability

  // ─── Init ────────────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', init);

  async function init() {
    setupNavigation();
    setupEventListeners();
    setupCurationListeners();
    setupCanonListeners();
    setupPortalControls();
    await loadServiceInfo();
    await loadRepos();
    checkEnrichmentAvailable();
    navigateToHash();
  }

  // ─── Navigation ──────────────────────────────────────────────────

  function setupNavigation() {
    window.addEventListener('hashchange', navigateToHash);
  }

  function navigateToHash() {
    var raw = location.hash.slice(1) || 'dashboard';
    // Slash-split routing: portal/<repo> and memory/<id>; everything else is a flat page name.
    var slash = raw.indexOf('/');
    if (slash > 0) {
      var head = raw.slice(0, slash);
      var tail = raw.slice(slash + 1);
      if (head === 'portal') { showPage('portal', decodeURIComponent(tail)); return; }
      if (head === 'memory') { showPage('memory', decodeURIComponent(tail)); return; }
    }
    showPage(raw);
  }

  function showPage(name, arg) {
    // Map sub-routes onto their host page id.
    var pageId = name === 'memory' ? 'browser' : name;
    var navKey = name === 'memory' ? 'browser' : name;

    document.querySelectorAll('.page').forEach(function (p) { p.classList.remove('active'); });
    document.querySelectorAll('.nav-link').forEach(function (l) { l.classList.remove('active'); });
    var page = document.getElementById('page-' + pageId);
    var link = document.querySelector('[data-page="' + navKey + '"]');
    if (page) page.classList.add('active');
    if (link) link.classList.add('active');
    if (pageId === 'dashboard') loadDashboard();
    else if (pageId === 'browser') {
      loadBrowser();
      if (name === 'memory' && arg) showDetail(arg);
    }
    else if (pageId === 'portal') loadPortal();
    else if (pageId === 'canon') loadCanon();
    else if (pageId === 'graph') loadGraph();
    else if (pageId === 'timeline') loadTimeline();
    else if (pageId === 'usage') loadUsage();
    else if (pageId === 'settings') loadSettings();
  }

  // ─── Event listeners ────────────────────────────────────────────

  function setupEventListeners() {
    document.getElementById('repoSelect').addEventListener('change', function () {
      currentRepo = this.value;
      navigateToHash();
    });

    document.getElementById('searchBtn').addEventListener('click', doSearch);
    document.getElementById('browseBtn').addEventListener('click', function () {
      browseSkip = 0;
      loadBrowser();
    });
    document.getElementById('searchInput').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') doSearch();
    });

    document.getElementById('graphRefresh').addEventListener('click', loadGraph);
    document.getElementById('graphLimit').addEventListener('input', function () {
      document.getElementById('graphLimitLabel').textContent = this.value;
    });
    setupGraphEventListeners();

    document.getElementById('btnIntake').addEventListener('click', function () { runAction('intake'); });
    document.getElementById('btnConsolidate').addEventListener('click', function () { runAction('consolidate'); });
    document.getElementById('btnMaintenance').addEventListener('click', function () { runAction('maintenance'); });
    document.getElementById('btnExport').addEventListener('click', function () { runAction('export'); });

    document.getElementById('usageDays').addEventListener('change', loadUsage);
    document.getElementById('btnCreateRepoLink').addEventListener('click', createRepoLink);
  }

  // ─── API helpers ─────────────────────────────────────────────────

  async function api(path) {
    var res = await fetch(API + path);
    if (!res.ok) throw new Error('API error: ' + res.status);
    return res.json();
  }

  async function apiPost(path) {
    var res = await fetch(API + path, { method: 'POST' });
    if (!res.ok) {
      var ct = res.headers.get('content-type') || '';
      if (ct.includes('json')) {
        var body = await res.json();
        throw new Error(body.error || 'API error: ' + res.status);
      }
      throw new Error('API error: ' + res.status);
    }
    var ct = res.headers.get('content-type') || '';
    if (ct.includes('json')) return res.json();
    return res.text();
  }

  function encRepo() {
    return encodeURIComponent(currentRepo);
  }

  // ─── Service info ────────────────────────────────────────────────

  async function loadServiceInfo() {
    try {
      var data = await api('/api/status');
      document.getElementById('serviceVersion').textContent = 'v' + data.version + ' | ' + data.uptime;
    } catch (_) {
      document.getElementById('serviceVersion').textContent = 'offline';
    }
    loadUpdateBanner();
  }

  // Reads the cached result of the nightly check — /api/health never hits the network itself.
  // Dismissal is remembered per version, so the banner comes back for the next release.
  async function loadUpdateBanner() {
    var banner = document.getElementById('updateBanner');
    if (!banner) return;

    try {
      var health = await api('/api/health');
      if (!health.updateAvailable) return;
      if (sessionStorage.getItem('eidet.updateDismissed') === health.latestVersion) return;

      document.getElementById('updateBannerText').textContent =
        'Eidet ' + health.latestVersion + ' is available (running ' + health.version + ') — ';
      banner.hidden = false;

      document.getElementById('updateBannerDismiss').onclick = function () {
        sessionStorage.setItem('eidet.updateDismissed', health.latestVersion);
        banner.hidden = true;
      };
    } catch (_) {
      // A health probe we can't read is not worth a UI error.
    }
  }

  // ─── Repos ───────────────────────────────────────────────────────

  async function loadRepos() {
    try {
      var data = await api('/api/eidet/repos');
      var select = document.getElementById('repoSelect');
      select.innerHTML = '';
      if (data.repos.length === 0) {
        select.innerHTML = '<option value="">No repos found</option>';
        return;
      }
      // Sort repos alphabetically by name and build path map
      repoPathMap = {};
      var repos = data.repos.slice().sort(function (a, b) {
        return a.repoId.localeCompare(b.repoId, undefined, { sensitivity: 'base' });
      });
      repos.forEach(function (r) {
        if (r.originalPath) repoPathMap[r.repoId] = r.originalPath;
        var opt = document.createElement('option');
        opt.value = r.repoId;
        opt.textContent = formatRepoDisplay(r.originalPath || r.repoId);
        opt.title = r.originalPath || r.repoId;
        select.appendChild(opt);
      });
      currentRepo = select.value;
    } catch (_) {
      document.getElementById('repoSelect').innerHTML = '<option value="">Error loading repos</option>';
    }
  }

  // ─── Dashboard ───────────────────────────────────────────────────

  async function loadDashboard() {
    if (!currentRepo) return;
    var grid = document.getElementById('dashboardStats');
    var list = document.getElementById('recentMemories');
    grid.innerHTML = '<div class="loading">Loading...</div>';

    try {
      var data = await api('/api/eidet/browse?repo=' + encRepo() + '&skip=0&take=10');
      var entries = data.entries;

      // Count by type from browse counts (recent 10 for display)
      var counts = { observation: 0, insight: 0, procedure: 0, heuristic: 0 };
      var total = 0;

      // Try to get accurate counts from a larger browse
      try {
        var allData = await api('/api/eidet/browse?repo=' + encRepo() + '&skip=0&take=1000');
        allData.entries.forEach(function (e) { counts[e.type] = (counts[e.type] || 0) + 1; });
        total = allData.entries.length;
      } catch (_) {
        entries.forEach(function (e) { counts[e.type] = (counts[e.type] || 0) + 1; });
        total = entries.length;
      }

      grid.innerHTML =
        statCard('Observations', counts.observation, 'observation') +
        statCard('Insights', counts.insight, 'insight') +
        statCard('Procedures', counts.procedure, 'procedure') +
        statCard('Heuristics', counts.heuristic, 'heuristic') +
        statCard('Total', total, '');

      // Recent
      list.innerHTML = entries.length > 0
        ? entries.map(memoryItemHtml).join('')
        : '<div class="empty-state">No memories yet</div>';

      setupMemoryClicks(list);

      // Load context preview
      loadContextPreview();
    } catch (_) {
      grid.innerHTML = '<div class="empty-state">Could not load dashboard</div>';
    }
  }

  async function loadContextPreview() {
    var el = document.getElementById('contextPreview');
    var crEl = document.getElementById('crossRepoInfo');
    if (!el) return;
    el.innerHTML = '<div class="loading">Loading context...</div>';

    try {
      var data = await api('/api/eidet/context/preview?repo=' + encRepo());
      var lines = (data.context || '').split('\n');
      var html = '<div class="context-header">';
      html += '<span class="context-tokens">~' + data.estimatedTokens + ' tokens</span>';
      html += '</div>';
      html += '<pre class="context-block">';
      lines.forEach(function (line) {
        if (line.startsWith('[I]')) {
          html += '<span class="ctx-insight">' + escHtml(line) + '</span>\n';
        } else if (line.startsWith('[P]')) {
          html += '<span class="ctx-procedure">' + escHtml(line) + '</span>\n';
        } else if (line.startsWith('[H]')) {
          html += '<span class="ctx-heuristic">' + escHtml(line) + '</span>\n';
        } else if (line.startsWith('[O]')) {
          html += '<span class="ctx-observation">' + escHtml(line) + '</span>\n';
        } else if (line.startsWith('[Memory:')) {
          html += '<span class="ctx-l0">' + escHtml(line) + '</span>\n';
        } else {
          html += escHtml(line) + '\n';
        }
      });
      html += '</pre>';
      el.innerHTML = html;

      // Cross-repo info
      if (crEl && data.crossRepoScope && data.crossRepoScope.length > 1) {
        var crHtml = '<div class="cross-repo-label">Cross-Repo Scope (' + data.crossRepoScope.length + ' repos)</div>';
        crHtml += '<div class="cross-repo-list">';
        data.crossRepoScope.forEach(function (r) {
          var normalized = r.replace(/\\/g, '/');
          var parts = normalized.split('/');
          var name = parts[parts.length - 1] || r;
          crHtml += '<span class="cross-repo-chip" title="' + escAttr(r) + '">' + escHtml(name) + '</span>';
        });
        crHtml += '</div>';
        crEl.innerHTML = crHtml;
      } else if (crEl) {
        crEl.innerHTML = '';
      }

      // Layers
      if (crEl && data.layers && data.layers.length > 0) {
        var layerHtml = '<div class="cross-repo-label" style="margin-top:8px">Mounted Layers (' + data.layers.length + ')</div>';
        layerHtml += '<div class="cross-repo-list">';
        data.layers.forEach(function (l) {
          layerHtml += '<span class="layer-chip" title="' + escAttr(l.id) + '">' + escHtml(l.name) + ' <span class="layer-type">' + escHtml(l.type) + '</span></span>';
        });
        layerHtml += '</div>';
        crEl.innerHTML += layerHtml;
      }
    } catch (_) {
      el.innerHTML = '<div class="empty-state">Could not load context preview</div>';
    }
  }

  function statCard(label, value, type) {
    return '<div class="stat-card ' + (type ? 'type-' + type : '') + '">' +
      '<div class="stat-value">' + value + '</div>' +
      '<div class="stat-label">' + label + '</div></div>';
  }

  // ─── Browser ─────────────────────────────────────────────────────

  async function loadBrowser() {
    if (!currentRepo) return;
    var results = document.getElementById('browserResults');
    results.innerHTML = '<div class="loading">Loading...</div>';

    try {
      var typeParam = document.getElementById('typeFilter').value;
      var url = '/api/eidet/browse?repo=' + encRepo() + '&skip=' + browseSkip + '&take=' + browseTake;
      if (typeParam) url += '&type=' + typeParam;
      var data = await api(url);

      if (data.entries.length === 0) {
        results.innerHTML = '<div class="empty-state">No memories found</div>';
      } else {
        results.innerHTML = data.entries.map(memoryItemHtml).join('');
        setupMemoryClicks(results);
      }

      renderPagination(data.count);
    } catch (_) {
      results.innerHTML = '<div class="empty-state">Error loading memories</div>';
    }
  }

  async function doSearch() {
    var q = document.getElementById('searchInput').value.trim();
    if (!q || !currentRepo) return;
    var results = document.getElementById('browserResults');
    results.innerHTML = '<div class="loading">Searching...</div>';

    try {
      var typeParam = document.getElementById('typeFilter').value;
      var url = '/api/eidet/search?repo=' + encRepo() + '&q=' + encodeURIComponent(q) + '&limit=50';
      if (typeParam) url += '&type=' + typeParam;
      var data = await api(url);

      if (data.results.length === 0) {
        results.innerHTML = '<div class="empty-state">No results for "' + escHtml(q) + '"</div>';
      } else {
        results.innerHTML = data.results.map(function (r) { return memoryItemHtml(r); }).join('');
        setupMemoryClicks(results);
      }
      document.getElementById('browserPagination').innerHTML = '';
    } catch (_) {
      results.innerHTML = '<div class="empty-state">Search error</div>';
    }
  }

  function renderPagination(pageCount) {
    var el = document.getElementById('browserPagination');
    var html = '';
    if (browseSkip > 0) {
      html += '<button class="btn" onclick="window.__eidetPrev()">Previous</button>';
    }
    if (pageCount >= browseTake) {
      html += '<button class="btn" onclick="window.__eidetNext()">Next</button>';
    }
    el.innerHTML = html;
  }

  window.__eidetPrev = function () { browseSkip = Math.max(0, browseSkip - browseTake); loadBrowser(); };
  window.__eidetNext = function () { browseSkip += browseTake; loadBrowser(); };

  // ─── Memory detail ───────────────────────────────────────────────

  function setupMemoryClicks(container) {
    container.querySelectorAll('.memory-item').forEach(function (item) {
      item.addEventListener('click', function () {
        container.querySelectorAll('.memory-item').forEach(function (i) { i.classList.remove('selected'); });
        this.classList.add('selected');
        showDetail(this.dataset.id);
      });
    });
  }

  async function showDetail(id) {
    var panel = document.getElementById('detailPanel');
    if (!panel) return;
    panel.innerHTML = '<div class="loading">Loading...</div>';

    try {
      var entry = await api('/api/eidet/' + encodeURIComponent(id));
      currentDetailEntry = entry;
      renderDetailView(entry);
    } catch (_) {
      panel.innerHTML = '<div class="detail-placeholder">Could not load memory details</div>';
    }
  }

  function renderDetailView(entry) {
    var panel = document.getElementById('detailPanel');
    if (!panel) return;

    var html = '';

    // Action bar
    html += '<div class="detail-actions">';
    html += '<button class="btn btn-primary" onclick="window.__eidetEdit()">Edit</button>';
    html += '<button class="btn btn-success" onclick="window.__eidetEcho()">Echo</button>';
    html += '<button class="btn btn-warning" onclick="window.__eidetFizzle()">Fizzle</button>';
    html += '<button class="btn btn-danger" onclick="window.__eidetForgetCurrent()">Forget</button>';
    html += '</div>';

    // Type
    html += '<div class="detail-section"><label>Type</label>' +
      '<span class="memory-type ' + entry.type + '">' + entry.type + '</span></div>';

    // Content
    html += '<div class="detail-section"><label>Content</label><div class="value">' + escHtml(entry.content) + '</div></div>';

    // Enrichments with AI buttons
    html += '<div class="detail-section"><label>One-liner</label>';
    html += '<div class="value">' + escHtml(entry.oneLiner || '(none)') + '</div>';
    if (enrichmentAvailable) {
      html += '<div class="ai-enrich-bar"><span class="ai-label">AI</span>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrich(\'oneliner\')">Regenerate</button>';
      html += '</div>';
    }
    html += '</div>';

    html += '<div class="detail-section"><label>Summary</label>';
    html += '<div class="value">' + escHtml(entry.summary || '(none)') + '</div>';
    if (enrichmentAvailable) {
      html += '<div class="ai-enrich-bar"><span class="ai-label">AI</span>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrich(\'summary\')">Regenerate</button>';
      html += '</div>';
    }
    html += '</div>';

    html += '<div class="detail-section"><label>Foresight</label>';
    html += '<div class="value">' + escHtml(entry.foresightHint || '(none)') + '</div>';
    if (enrichmentAvailable) {
      html += '<div class="ai-enrich-bar"><span class="ai-label">AI</span>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrich(\'foresight\')">Regenerate</button>';
      html += '</div>';
    }
    html += '</div>';

    // AI result area (shared for enrichment output)
    html += '<div id="aiResultArea" style="display:none"></div>';

    // Tags
    html += '<div class="detail-section"><label>Tags</label><div class="memory-tags">' +
      (entry.tags || []).map(function (t) { return '<span class="tag">' + escHtml(t) + '</span>'; }).join('') +
      '</div></div>';

    // Entities
    html += '<div class="detail-section"><label>Entities</label><div class="value">' +
      (entry.entities || []).map(function (e) { return escHtml(e); }).join(', ') +
      '</div></div>';

    // Links
    html += '<div class="detail-section"><label>Links</label>';
    if (entry.links && entry.links.length > 0) {
      html += '<div class="link-list">';
      entry.links.forEach(function (l) {
        html += '<div class="link-item">';
        html += '<span class="link-relation">' + escHtml(l.relation) + '</span>';
        html += '<span class="link-target">' + escHtml(l.targetMemoryId || l.targetRepoId) + '</span>';
        html += '<span class="link-remove" title="Remove link" onclick="window.__eidetRemoveLink(\'' + escAttr(l.targetRepoId) + '\',\'' + escAttr(l.relation) + '\')">&times;</span>';
        html += '</div>';
      });
      html += '</div>';
    } else {
      html += '<div class="value" style="color:var(--text-muted)">(no links)</div>';
    }
    html += '</div>';

    // Meta grid
    html += '<div class="detail-meta">' +
      metaItem('Importance', (entry.importance * 100).toFixed(0) + '%') +
      metaItem('Confidence', (entry.confidence * 100).toFixed(0) + '%') +
      metaItem('Accessed', entry.accessCount + 'x') +
      metaItem('Echoes', entry.echoCount + ' / Fizzles: ' + entry.fizzleCount) +
      metaItem('Created', formatDate(entry.createdAt)) +
      metaItem('Provenance', entry.provenance || '--') +
      metaItem('Source', entry.source || '--') +
      metaItem('ID', '<span style="font-size:10px;word-break:break-all">' + escHtml(entry.id) + '</span>') +
      '</div>';

    panel.innerHTML = html;
  }

  function renderEditView(entry) {
    var panel = document.getElementById('detailPanel');
    if (!panel) return;

    var html = '';
    html += '<h3 style="margin-bottom:12px">Edit Memory</h3>';

    // Type
    html += '<div class="edit-form-group"><label>Type</label>';
    html += '<select id="editType" class="edit-select">';
    ['observation', 'insight', 'procedure', 'heuristic'].forEach(function (t) {
      html += '<option value="' + t + '"' + (entry.type === t ? ' selected' : '') + '>' + t + '</option>';
    });
    html += '</select></div>';

    // Content
    html += '<div class="edit-form-group"><label>Content</label>';
    html += '<textarea id="editContent" class="edit-textarea" rows="6">' + escHtml(entry.content) + '</textarea>';
    if (enrichmentAvailable) {
      html += '<div class="ai-enrich-bar" style="margin-top:6px"><span class="ai-label">AI</span>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrichEdit(\'oneliner\')">Gen One-liner</button>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrichEdit(\'summary\')">Gen Summary</button>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrichEdit(\'foresight\')">Gen Foresight</button>';
      html += '<button class="btn btn-ai" onclick="window.__eidetAiEnrichEdit(\'entities\')">Extract Entities</button>';
      html += '</div>';
    }
    html += '<div id="editAiResult" style="display:none"></div>';
    html += '</div>';

    // Tags
    html += '<div class="edit-form-group"><label>Tags (comma-separated)</label>';
    html += '<input type="text" id="editTags" class="edit-input" value="' + escAttr((entry.tags || []).join(', ')) + '"></div>';

    // Importance
    html += '<div class="edit-form-group"><label>Importance</label>';
    html += '<div class="edit-range-wrap">';
    html += '<input type="range" id="editImportance" min="0" max="100" value="' + Math.round(entry.importance * 100) + '">';
    html += '<span class="edit-range-val" id="editImpVal">' + Math.round(entry.importance * 100) + '%</span>';
    html += '</div></div>';

    // Confidence
    html += '<div class="edit-form-group"><label>Confidence</label>';
    html += '<div class="edit-range-wrap">';
    html += '<input type="range" id="editConfidence" min="0" max="100" value="' + Math.round(entry.confidence * 100) + '">';
    html += '<span class="edit-range-val" id="editConfVal">' + Math.round(entry.confidence * 100) + '%</span>';
    html += '</div></div>';

    // Actions
    html += '<div class="edit-actions">';
    html += '<button class="btn" onclick="window.__eidetCancelEdit()">Cancel</button>';
    html += '<button class="btn btn-primary" onclick="window.__eidetSaveEdit()">Save Changes</button>';
    html += '</div>';

    html += '<div id="editResult" style="display:none" class="edit-result"></div>';

    panel.innerHTML = html;

    // Wire range value display
    document.getElementById('editImportance').addEventListener('input', function () {
      document.getElementById('editImpVal').textContent = this.value + '%';
    });
    document.getElementById('editConfidence').addEventListener('input', function () {
      document.getElementById('editConfVal').textContent = this.value + '%';
    });
  }

  function metaItem(label, value) {
    return '<div class="detail-section"><label>' + label + '</label><div class="value">' + value + '</div></div>';
  }

  // ─── Curation actions ────────────────────────────────────────────────

  function setupCurationListeners() {
    // Create memory dialog
    document.getElementById('createMemoryBtn').addEventListener('click', function () {
      document.getElementById('createMemoryDialog').style.display = '';
      document.getElementById('createMemoryResult').textContent = '';
    });
    document.getElementById('cancelCreateMemory').addEventListener('click', function () {
      document.getElementById('createMemoryDialog').style.display = 'none';
    });
    document.getElementById('confirmCreateMemory').addEventListener('click', createMemory);
    document.getElementById('newMemoryImportance').addEventListener('input', function () {
      document.getElementById('newMemoryImpLabel').textContent = this.value + '%';
    });
    // Close dialog on overlay click
    document.getElementById('createMemoryDialog').addEventListener('click', function (e) {
      if (e.target === this) this.style.display = 'none';
    });
  }

  async function createMemory() {
    var result = document.getElementById('createMemoryResult');
    var content = document.getElementById('newMemoryContent').value.trim();
    if (content.length < 20) {
      result.className = 'form-result error';
      result.textContent = 'Content must be at least 20 characters.';
      return;
    }
    var tagsStr = document.getElementById('newMemoryTags').value.trim();
    var tags = tagsStr ? tagsStr.split(',').map(function (t) { return t.trim(); }).filter(Boolean) : [];
    var importance = parseInt(document.getElementById('newMemoryImportance').value) / 100;
    var type = document.getElementById('newMemoryType').value;

    result.className = 'form-result';
    result.textContent = 'Creating...';

    try {
      var res = await fetch(API + '/api/eidet', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          repo: repoPathMap[currentRepo] || currentRepo,
          content: content,
          type: type,
          tags: tags.length > 0 ? tags : null,
          importance: importance,
          source: 'web-ui',
        }),
      });
      var data = await res.json();
      if (res.ok) {
        result.className = 'form-result success';
        result.textContent = 'Created: ' + data.id;
        document.getElementById('newMemoryContent').value = '';
        document.getElementById('newMemoryTags').value = '';
        setTimeout(function () {
          document.getElementById('createMemoryDialog').style.display = 'none';
          loadBrowser();
        }, 1000);
      } else {
        result.className = 'form-result error';
        result.textContent = data.error || 'Failed to create memory';
      }
    } catch (e) {
      result.className = 'form-result error';
      result.textContent = 'Error: ' + e.message;
    }
  }

  // Global action handlers
  window.__eidetEdit = function () {
    if (currentDetailEntry) renderEditView(currentDetailEntry);
  };

  window.__eidetCancelEdit = function () {
    if (currentDetailEntry) renderDetailView(currentDetailEntry);
  };

  window.__eidetSaveEdit = async function () {
    if (!currentDetailEntry) return;
    var resultEl = document.getElementById('editResult');
    resultEl.style.display = '';
    resultEl.className = 'edit-result';
    resultEl.textContent = 'Saving...';

    var newContent = document.getElementById('editContent').value.trim();
    var newTags = document.getElementById('editTags').value.trim();
    var tags = newTags ? newTags.split(',').map(function (t) { return t.trim(); }).filter(Boolean) : [];
    var importance = parseInt(document.getElementById('editImportance').value) / 100;
    var confidence = parseInt(document.getElementById('editConfidence').value) / 100;
    var type = document.getElementById('editType').value;

    var body = {};
    if (newContent !== currentDetailEntry.content) body.content = newContent;
    if (JSON.stringify(tags) !== JSON.stringify(currentDetailEntry.tags || [])) body.tags = tags;
    if (Math.abs(importance - currentDetailEntry.importance) > 0.005) body.importance = importance;
    if (Math.abs(confidence - currentDetailEntry.confidence) > 0.005) body.confidence = confidence;
    if (type !== currentDetailEntry.type) body.type = type;

    if (Object.keys(body).length === 0) {
      resultEl.className = 'edit-result';
      resultEl.textContent = 'No changes detected.';
      return;
    }

    try {
      var res = await fetch(API + '/api/eidet/' + encodeURIComponent(currentDetailEntry.id), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      var data = await res.json();
      if (res.ok) {
        resultEl.className = 'edit-result success';
        resultEl.textContent = 'Saved successfully.';
        // Refresh the detail view
        setTimeout(function () {
          showDetail(currentDetailEntry.id);
          loadBrowser();
        }, 500);
      } else {
        resultEl.className = 'edit-result error';
        resultEl.textContent = data.error || 'Failed to save';
      }
    } catch (e) {
      resultEl.className = 'edit-result error';
      resultEl.textContent = 'Error: ' + e.message;
    }
  };

  window.__eidetEcho = async function () {
    if (!currentDetailEntry) return;
    try {
      await fetch(API + '/api/eidet/feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ memoryId: currentDetailEntry.id, wasUsed: true }),
      });
      showDetail(currentDetailEntry.id);
    } catch (_) {}
  };

  window.__eidetFizzle = async function () {
    if (!currentDetailEntry) return;
    try {
      await fetch(API + '/api/eidet/feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ memoryId: currentDetailEntry.id, wasUsed: false }),
      });
      showDetail(currentDetailEntry.id);
    } catch (_) {}
  };

  window.__eidetForgetCurrent = async function () {
    if (!currentDetailEntry) return;
    if (!confirm('Forget this memory? This soft-deletes it with an audit trail.')) return;
    try {
      await fetch(API + '/api/eidet/' + encodeURIComponent(currentDetailEntry.id), { method: 'DELETE' });
      currentDetailEntry = null;
      document.getElementById('detailPanel').innerHTML = '<div class="detail-placeholder">Memory forgotten.</div>';
      loadBrowser();
    } catch (_) {}
  };

  window.__eidetRemoveLink = async function (targetRepoId, relation) {
    if (!currentDetailEntry) return;
    if (!confirm('Remove this link?')) return;
    try {
      await fetch(API + '/api/eidet/' + encodeURIComponent(currentDetailEntry.id) +
        '/links?targetRepoId=' + encodeURIComponent(targetRepoId) +
        '&relation=' + encodeURIComponent(relation), { method: 'DELETE' });
      showDetail(currentDetailEntry.id);
    } catch (_) {}
  };

  // ─── AI Enrichment ──────────────────────────────────────────────────

  async function checkEnrichmentAvailable() {
    try {
      var data = await api('/api/status');
      enrichmentAvailable = data.ollama && data.ollama.enabled && data.ollama.healthy;
    } catch (_) {
      enrichmentAvailable = false;
    }
  }

  window.__eidetAiEnrich = async function (task) {
    if (!currentDetailEntry) return;
    var areaEl = document.getElementById('aiResultArea');
    if (!areaEl) return;
    areaEl.style.display = '';
    areaEl.innerHTML = '<div class="ai-result" style="color:var(--text-muted)">Generating ' + escHtml(task) + '...</div>';

    try {
      var res = await fetch(API + '/api/eidet/enrich', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: currentDetailEntry.content, task: task }),
      });
      var data = await res.json();
      if (res.ok && data.result) {
        areaEl.innerHTML = '<div class="ai-result">' + escHtml(data.result) + '</div>' +
          '<div class="ai-result-actions">' +
          '<button class="btn btn-ai" onclick="window.__eidetApplyEnrichment(\'' + escAttr(task) + '\')">Apply to memory</button>' +
          '</div>';
      } else {
        areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">' + escHtml(data.error || 'Failed') + '</div>';
      }
    } catch (e) {
      areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">Error: ' + escHtml(e.message) + '</div>';
    }
  };

  window.__eidetAiEnrichEdit = async function (task) {
    var areaEl = document.getElementById('editAiResult');
    if (!areaEl) return;
    var content = document.getElementById('editContent').value.trim();
    if (!content) return;
    areaEl.style.display = '';
    areaEl.innerHTML = '<div class="ai-result" style="color:var(--text-muted)">Generating ' + escHtml(task) + '...</div>';

    try {
      var res = await fetch(API + '/api/eidet/enrich', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: content, task: task }),
      });
      var data = await res.json();
      if (res.ok && data.result) {
        areaEl.innerHTML = '<div class="ai-result"><strong>' + escHtml(task) + ':</strong> ' + escHtml(data.result) + '</div>';
      } else {
        areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">' + escHtml(data.error || 'Failed') + '</div>';
      }
    } catch (e) {
      areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">Error: ' + escHtml(e.message) + '</div>';
    }
  };

  window.__eidetApplyEnrichment = async function (task) {
    if (!currentDetailEntry) return;
    var areaEl = document.getElementById('aiResultArea');
    if (!areaEl) return;
    var resultDiv = areaEl.querySelector('.ai-result');
    if (!resultDiv) return;
    var text = resultDiv.textContent;

    // Map task to update field
    var body = {};
    if (task === 'oneliner') body.oneLiner = text;
    else if (task === 'summary') body.summary = text;
    else if (task === 'foresight') body.foresightHint = text;
    else return;

    try {
      var res = await fetch(API + '/api/eidet/' + encodeURIComponent(currentDetailEntry.id), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (res.ok) {
        areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-success)">Applied successfully.</div>';
        setTimeout(function () { showDetail(currentDetailEntry.id); }, 500);
      } else {
        var data = await res.json();
        areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">' + escHtml(data.error || 'Failed to apply') + '</div>';
      }
    } catch (e) {
      areaEl.innerHTML = '<div class="ai-result" style="color:var(--color-error)">Error: ' + escHtml(e.message) + '</div>';
    }
  };

  // ─── Graph ───────────────────────────────────────────────────────

  var graphSim = null;
  var graphState = {
    nodes: [], edges: [], allNodes: [], allEdges: [],
    nodeMap: {},
    selectedNode: null, hoveredNode: null, dragging: null,
    // Camera (pan + zoom)
    camX: 0, camY: 0, zoom: 1,
    isPanning: false, panStartX: 0, panStartY: 0, camStartX: 0, camStartY: 0,
    mouseX: 0, mouseY: 0,
    alpha: 1,
    // Adjacency for highlighting
    adjacency: {},    // nodeId -> Set<nodeId>
    edgeByPair: {},   // "from|to" -> edge
    // Type filters
    typeFilters: { observation: true, insight: true, procedure: true, heuristic: true }
  };

  var typeColors = {
    observation: '#5b9cf6',
    insight: '#a87cff',
    procedure: '#4ecb8d',
    heuristic: '#f0a54a'
  };

  var typeColorsDim = {
    observation: 'rgba(91,156,246,0.2)',
    insight: 'rgba(168,124,255,0.2)',
    procedure: 'rgba(78,203,141,0.2)',
    heuristic: 'rgba(240,165,74,0.2)'
  };

  async function loadGraph() {
    if (!currentRepo) return;
    var canvas = document.getElementById('graphCanvas');
    var ctx = canvas.getContext('2d');
    var wrap = canvas.parentElement;
    canvas.width = wrap.clientWidth;
    canvas.height = wrap.clientHeight || 600;

    ctx.fillStyle = '#0f1117';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#5a5e72';
    ctx.font = '14px -apple-system, system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('Loading graph...', canvas.width / 2, canvas.height / 2);

    try {
      var limit = parseInt(document.getElementById('graphLimit').value) || 100;
      var data = await api('/api/eidet/graph?repo=' + encRepo() + '&limit=' + limit);
      setupGraph(canvas, data);
    } catch (_) {
      ctx.fillStyle = '#0f1117';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#5a5e72';
      ctx.fillText('Could not load graph data', canvas.width / 2, canvas.height / 2);
    }
  }

  function setupGraphEventListeners() {
    // Type filter checkboxes
    document.querySelectorAll('[data-graph-type]').forEach(function (cb) {
      cb.addEventListener('change', function () {
        graphState.typeFilters[this.dataset.graphType] = this.checked;
        applyGraphFilters();
      });
    });
    document.getElementById('graphZoomIn').addEventListener('click', function () {
      graphState.zoom = Math.min(5, graphState.zoom * 1.3);
      graphState.alpha = Math.max(graphState.alpha, 0.02);
      requestAnimationFrame(graphTick);
    });
    document.getElementById('graphZoomOut').addEventListener('click', function () {
      graphState.zoom = Math.max(0.1, graphState.zoom / 1.3);
      graphState.alpha = Math.max(graphState.alpha, 0.02);
      requestAnimationFrame(graphTick);
    });
    document.getElementById('graphZoomReset').addEventListener('click', function () {
      graphState.zoom = 1;
      graphState.camX = 0;
      graphState.camY = 0;
      graphState.alpha = Math.max(graphState.alpha, 0.02);
      requestAnimationFrame(graphTick);
    });
  }

  function applyGraphFilters() {
    var g = graphState;
    g.nodes = g.allNodes.filter(function (n) { return g.typeFilters[n.type]; });
    var visibleIds = {};
    g.nodes.forEach(function (n) { visibleIds[n.id] = true; });
    g.edges = g.allEdges.filter(function (e) { return visibleIds[e.from] && visibleIds[e.to]; });
    g.nodeMap = {};
    g.nodes.forEach(function (n) { g.nodeMap[n.id] = n; });
    buildAdjacency();
    // If selected node was filtered out, deselect
    if (g.selectedNode && !visibleIds[g.selectedNode.id]) {
      g.selectedNode = null;
      showGraphDetail(null);
    }
    g.alpha = Math.max(g.alpha, 0.5);
    requestAnimationFrame(graphTick);
  }

  function buildAdjacency() {
    var g = graphState;
    g.adjacency = {};
    g.edgeByPair = {};
    g.nodes.forEach(function (n) { g.adjacency[n.id] = new Set(); });
    g.edges.forEach(function (e) {
      if (g.adjacency[e.from]) g.adjacency[e.from].add(e.to);
      if (g.adjacency[e.to]) g.adjacency[e.to].add(e.from);
      g.edgeByPair[e.from + '|' + e.to] = e;
      g.edgeByPair[e.to + '|' + e.from] = e;
    });
  }

  function nodeRadius(n) { return 6 + n.importance * 14; }

  function screenToWorld(sx, sy, canvas) {
    var g = graphState;
    var cx = canvas.width / 2;
    var cy = canvas.height / 2;
    return {
      x: (sx - cx) / g.zoom + cx - g.camX,
      y: (sy - cy) / g.zoom + cy - g.camY
    };
  }

  function worldToScreen(wx, wy, canvas) {
    var g = graphState;
    var cx = canvas.width / 2;
    var cy = canvas.height / 2;
    return {
      x: (wx - cx + g.camX) * g.zoom + cx,
      y: (wy - cy + g.camY) * g.zoom + cy
    };
  }

  function hitTestNode(worldX, worldY) {
    var g = graphState;
    for (var i = g.nodes.length - 1; i >= 0; i--) {
      var n = g.nodes[i];
      var dx = n.x - worldX, dy = n.y - worldY;
      var r = nodeRadius(n) + 4; // small click padding
      if (dx * dx + dy * dy < r * r) return n;
    }
    return null;
  }

  function setupGraph(canvas, data) {
    var ctx = canvas.getContext('2d');
    var g = graphState;
    var W = canvas.width;
    var H = canvas.height;

    // Build node data with physics
    g.allNodes = data.nodes.map(function (n) {
      return {
        id: n.id, type: n.type, label: n.label,
        importance: n.importance, confidence: n.confidence,
        createdAt: n.createdAt, accessCount: n.accessCount,
        echoCount: n.echoCount, fizzleCount: n.fizzleCount,
        tags: n.tags || [], entities: n.entities || [],
        x: W / 2 + (Math.random() - 0.5) * W * 0.5,
        y: H / 2 + (Math.random() - 0.5) * H * 0.5,
        vx: 0, vy: 0
      };
    });
    g.allEdges = data.edges.slice();
    g.selectedNode = null;
    g.hoveredNode = null;
    g.dragging = null;
    g.alpha = 1;
    g.camX = 0; g.camY = 0; g.zoom = 1;

    applyGraphFilters();

    if (graphSim) cancelAnimationFrame(graphSim);

    // ─── Mouse events ─────────────────────────────────────────
    canvas.onmousedown = function (e) {
      var rect = canvas.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sy = e.clientY - rect.top;
      var world = screenToWorld(sx, sy, canvas);
      var hit = hitTestNode(world.x, world.y);
      if (hit) {
        g.dragging = hit;
      } else {
        // Start panning
        g.isPanning = true;
        g.panStartX = sx;
        g.panStartY = sy;
        g.camStartX = g.camX;
        g.camStartY = g.camY;
      }
    };

    canvas.onmousemove = function (e) {
      var rect = canvas.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sy = e.clientY - rect.top;
      g.mouseX = sx;
      g.mouseY = sy;

      if (g.isPanning) {
        g.camX = g.camStartX + (sx - g.panStartX) / g.zoom;
        g.camY = g.camStartY + (sy - g.panStartY) / g.zoom;
        g.alpha = Math.max(g.alpha, 0.02);
        requestAnimationFrame(graphTick);
        return;
      }

      if (g.dragging) {
        var world = screenToWorld(sx, sy, canvas);
        g.dragging.x = world.x;
        g.dragging.y = world.y;
        g.dragging.vx = 0;
        g.dragging.vy = 0;
        g.alpha = Math.max(g.alpha, 0.1);
        requestAnimationFrame(graphTick);
        return;
      }

      var world = screenToWorld(sx, sy, canvas);
      var prev = g.hoveredNode;
      g.hoveredNode = hitTestNode(world.x, world.y);
      canvas.style.cursor = g.hoveredNode ? 'pointer' : (g.isPanning ? 'grabbing' : 'grab');
      if (g.hoveredNode !== prev) {
        g.alpha = Math.max(g.alpha, 0.02);
        requestAnimationFrame(graphTick);
      }
    };

    canvas.onmouseup = function (e) {
      if (g.isPanning) {
        g.isPanning = false;
        canvas.style.cursor = 'grab';
      }
      if (g.dragging) {
        // If barely moved, treat as click
        var rect = canvas.getBoundingClientRect();
        var sx = e.clientX - rect.left;
        var sy = e.clientY - rect.top;
        var world = screenToWorld(sx, sy, canvas);
        var hit = hitTestNode(world.x, world.y);
        if (hit && hit === g.dragging) {
          g.selectedNode = (g.selectedNode === hit) ? null : hit;
          showGraphDetail(g.selectedNode);
          g.alpha = Math.max(g.alpha, 0.02);
          requestAnimationFrame(graphTick);
        }
        g.dragging = null;
      }
    };

    canvas.onmouseleave = function () {
      g.dragging = null;
      g.isPanning = false;
      if (g.hoveredNode) {
        g.hoveredNode = null;
        g.alpha = Math.max(g.alpha, 0.02);
        requestAnimationFrame(graphTick);
      }
    };

    canvas.onwheel = function (e) {
      e.preventDefault();
      var factor = e.deltaY < 0 ? 1.1 : 0.9;
      g.zoom = Math.max(0.1, Math.min(5, g.zoom * factor));
      g.alpha = Math.max(g.alpha, 0.02);
      requestAnimationFrame(graphTick);
    };

    showGraphDetail(null);
    graphTick();
  }

  function graphTick() {
    var g = graphState;
    var canvas = document.getElementById('graphCanvas');
    if (!canvas) return;
    var ctx = canvas.getContext('2d');
    var W = canvas.width;
    var H = canvas.height;
    var nodes = g.nodes;
    var edges = g.edges;

    if (g.alpha > 0.01) {
      g.alpha *= 0.995;

      // Center gravity
      var cx = W / 2, cy = H / 2;
      nodes.forEach(function (n) {
        n.vx += (cx - n.x) * 0.0003;
        n.vy += (cy - n.y) * 0.0003;
      });

      // Node repulsion (Barnes-Hut approximation for large graphs: skip distant pairs)
      for (var i = 0; i < nodes.length; i++) {
        for (var j = i + 1; j < nodes.length; j++) {
          var a = nodes[i], b = nodes[j];
          var dx = b.x - a.x;
          var dy = b.y - a.y;
          var dist = Math.sqrt(dx * dx + dy * dy) || 1;
          if (dist > 600) continue; // skip very distant nodes
          var repulsion = -400 / (dist * dist);
          var fx = dx / dist * repulsion * g.alpha;
          var fy = dy / dist * repulsion * g.alpha;
          a.vx -= fx; a.vy -= fy;
          b.vx += fx; b.vy += fy;
        }
      }

      // Edge attraction
      edges.forEach(function (e) {
        var a = g.nodeMap[e.from], b = g.nodeMap[e.to];
        if (!a || !b) return;
        var dx = b.x - a.x;
        var dy = b.y - a.y;
        var dist = Math.sqrt(dx * dx + dy * dy) || 1;
        var targetLen = 100 + (nodeRadius(a) + nodeRadius(b));
        var force = (dist - targetLen) * 0.008 * g.alpha;
        var fx = dx / dist * force;
        var fy = dy / dist * force;
        a.vx += fx; a.vy += fy;
        b.vx -= fx; b.vy -= fy;
      });

      // Apply velocity
      nodes.forEach(function (n) {
        if (n === g.dragging) return;
        n.vx *= 0.82;
        n.vy *= 0.82;
        n.x += n.vx;
        n.y += n.vy;
      });
    }

    // ─── Render ────────────────────────────────────────────────

    ctx.save();
    ctx.fillStyle = '#0f1117';
    ctx.fillRect(0, 0, W, H);

    // Apply camera transform
    ctx.translate(W / 2, H / 2);
    ctx.scale(g.zoom, g.zoom);
    ctx.translate(-W / 2 + g.camX, -H / 2 + g.camY);

    var focusNode = g.selectedNode || g.hoveredNode;
    var neighborIds = null;
    if (focusNode && g.adjacency[focusNode.id]) {
      neighborIds = g.adjacency[focusNode.id];
    }

    // ─── Edges ─────────────────────────────────────────────
    edges.forEach(function (e) {
      var a = g.nodeMap[e.from], b = g.nodeMap[e.to];
      if (!a || !b) return;

      var isHighlighted = focusNode && (
        e.from === focusNode.id || e.to === focusNode.id
      );
      var isDimmed = focusNode && !isHighlighted;

      if (isDimmed) {
        ctx.strokeStyle = 'rgba(108,140,255,0.04)';
        ctx.lineWidth = 0.5;
      } else if (isHighlighted) {
        ctx.strokeStyle = 'rgba(108,140,255,0.7)';
        ctx.lineWidth = 2;
      } else {
        ctx.strokeStyle = 'rgba(108,140,255,0.2)';
        ctx.lineWidth = 1;
      }

      // Draw edge line
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.stroke();

      // Draw arrow at 70% along edge (towards 'to' node)
      if (isHighlighted || !focusNode) {
        var mx = a.x + (b.x - a.x) * 0.7;
        var my = a.y + (b.y - a.y) * 0.7;
        var angle = Math.atan2(b.y - a.y, b.x - a.x);
        var arrowLen = isHighlighted ? 8 : 5;
        ctx.fillStyle = ctx.strokeStyle;
        ctx.beginPath();
        ctx.moveTo(mx + Math.cos(angle) * arrowLen, my + Math.sin(angle) * arrowLen);
        ctx.lineTo(mx + Math.cos(angle + 2.5) * arrowLen, my + Math.sin(angle + 2.5) * arrowLen);
        ctx.lineTo(mx + Math.cos(angle - 2.5) * arrowLen, my + Math.sin(angle - 2.5) * arrowLen);
        ctx.closePath();
        ctx.fill();
      }

      // Edge label (relation) for highlighted edges
      if (isHighlighted && e.relation && g.zoom > 0.5) {
        var lx = (a.x + b.x) / 2;
        var ly = (a.y + b.y) / 2;
        ctx.font = (10 / Math.max(g.zoom, 0.5)) + 'px -apple-system, system-ui, sans-serif';
        ctx.fillStyle = 'rgba(108,140,255,0.8)';
        ctx.textAlign = 'center';
        ctx.fillText(e.relation, lx, ly - 6);
      }
    });

    // ─── Nodes ─────────────────────────────────────────────
    nodes.forEach(function (n) {
      var r = nodeRadius(n);
      var baseColor = typeColors[n.type] || '#5a5e72';
      var dimColor = typeColorsDim[n.type] || 'rgba(90,94,114,0.2)';

      var isSelected = n === g.selectedNode;
      var isHovered = n === g.hoveredNode;
      var isNeighbor = focusNode && neighborIds && neighborIds.has(n.id);
      var isFocus = n === focusNode;
      var isDimmed = focusNode && !isFocus && !isNeighbor;

      // Confidence affects opacity
      var baseAlpha = 0.4 + n.confidence * 0.6;
      var alpha = isDimmed ? 0.12 : baseAlpha;

      // Draw glow for selected/hovered
      if (isSelected || isHovered) {
        ctx.beginPath();
        ctx.arc(n.x, n.y, r + 8, 0, Math.PI * 2);
        var grad = ctx.createRadialGradient(n.x, n.y, r, n.x, n.y, r + 8);
        grad.addColorStop(0, baseColor.replace(')', ',0.3)').replace('rgb', 'rgba'));
        grad.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = grad;
        ctx.fill();
      }

      // Main node circle
      ctx.beginPath();
      ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
      ctx.fillStyle = isDimmed ? dimColor : colorWithAlpha(baseColor, alpha);
      ctx.fill();

      // Echo ring — golden ring for high echo count
      if (n.echoCount >= 3 && !isDimmed) {
        ctx.beginPath();
        ctx.arc(n.x, n.y, r + 2, 0, Math.PI * 2);
        ctx.strokeStyle = 'rgba(240,165,74,' + Math.min(1, 0.3 + n.echoCount * 0.1) + ')';
        ctx.lineWidth = 2;
        ctx.stroke();
      }

      // Selection/hover ring
      if (isSelected) {
        ctx.beginPath();
        ctx.arc(n.x, n.y, r + 4, 0, Math.PI * 2);
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2.5;
        ctx.stroke();
      } else if (isHovered) {
        ctx.beginPath();
        ctx.arc(n.x, n.y, r + 3, 0, Math.PI * 2);
        ctx.strokeStyle = 'rgba(255,255,255,0.6)';
        ctx.lineWidth = 1.5;
        ctx.stroke();
      }

      // Node label — show for important or zoomed-in nodes
      var showLabel = (!isDimmed) && (
        isFocus || isNeighbor || isSelected || isHovered ||
        n.importance >= 0.7 ||
        g.zoom >= 1.5 ||
        (g.zoom >= 0.8 && nodes.length < 40)
      );
      if (showLabel) {
        var labelText = truncate(n.label, 40);
        var fontSize = Math.max(9, Math.min(13, 11 / Math.max(g.zoom, 0.5)));
        ctx.font = fontSize + 'px -apple-system, system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillStyle = isDimmed ? 'rgba(228,230,237,0.15)' : 'rgba(228,230,237,0.9)';
        ctx.fillText(labelText, n.x, n.y + r + fontSize + 2);
      }
    });

    ctx.restore(); // undo camera transform

    // ─── Tooltip (screen-space) for hovered node ─────────
    if (g.hoveredNode && !g.dragging) {
      var hn = g.hoveredNode;
      var sp = worldToScreen(hn.x, hn.y, canvas);
      var tooltipLines = [hn.label];
      tooltipLines.push(hn.type + ' | imp: ' + (hn.importance * 100).toFixed(0) + '% | conf: ' + (hn.confidence * 100).toFixed(0) + '%');
      if (hn.tags.length > 0) tooltipLines.push(hn.tags.slice(0, 5).join(', '));
      if (hn.echoCount > 0 || hn.fizzleCount > 0) tooltipLines.push('echo: ' + hn.echoCount + ' | fizzle: ' + hn.fizzleCount);

      ctx.font = '12px -apple-system, system-ui, sans-serif';
      var maxW = 0;
      tooltipLines.forEach(function (l) { maxW = Math.max(maxW, ctx.measureText(l).width); });
      var ttW = maxW + 16;
      var ttH = tooltipLines.length * 18 + 12;
      var ttX = Math.min(sp.x - ttW / 2, W - ttW - 8);
      ttX = Math.max(8, ttX);
      var ttY = sp.y - nodeRadius(hn) * g.zoom - ttH - 8;
      if (ttY < 8) ttY = sp.y + nodeRadius(hn) * g.zoom + 8;

      // Background
      ctx.fillStyle = 'rgba(15,17,23,0.95)';
      ctx.strokeStyle = 'rgba(108,140,255,0.3)';
      ctx.lineWidth = 1;
      roundRect(ctx, ttX, ttY, ttW, ttH, 6);
      ctx.fill();
      ctx.stroke();

      // Text
      ctx.textAlign = 'left';
      tooltipLines.forEach(function (line, idx) {
        ctx.fillStyle = idx === 0 ? '#e4e6ed' : '#8b8fa3';
        ctx.font = idx === 0 ? 'bold 12px -apple-system, system-ui, sans-serif' : '11px -apple-system, system-ui, sans-serif';
        ctx.fillText(line, ttX + 8, ttY + 16 + idx * 18);
      });
    }

    // Schedule next frame if still simulating
    if (g.alpha > 0.01) {
      graphSim = requestAnimationFrame(graphTick);
    }
  }

  function showGraphDetail(node) {
    var panel = document.getElementById('graphDetailPanel');
    if (!panel) return;
    if (!node) {
      panel.innerHTML = '<div class="graph-detail-placeholder">Click a node to view details</div>';
      return;
    }

    var color = typeColors[node.type] || '#5a5e72';
    var g = graphState;
    var neighbors = g.adjacency[node.id] ? Array.from(g.adjacency[node.id]) : [];
    var neighborNodes = neighbors.map(function (id) { return g.nodeMap[id]; }).filter(Boolean);
    var dateStr = node.createdAt ? formatDate(node.createdAt) : '--';
    var age = node.createdAt ? daysSince(node.createdAt) : '--';

    var html = '';
    html += '<div class="gd-type-badge" style="background:' + color + '20;color:' + color + '">' + node.type + '</div>';
    html += '<div class="gd-label">' + escHtml(node.label) + '</div>';

    // Metrics row
    html += '<div class="gd-metrics">';
    html += '<div class="gd-metric"><span class="gd-metric-val">' + (node.importance * 100).toFixed(0) + '%</span><span class="gd-metric-lbl">Importance</span></div>';
    html += '<div class="gd-metric"><span class="gd-metric-val">' + (node.confidence * 100).toFixed(0) + '%</span><span class="gd-metric-lbl">Confidence</span></div>';
    html += '<div class="gd-metric"><span class="gd-metric-val">' + node.accessCount + '</span><span class="gd-metric-lbl">Accessed</span></div>';
    html += '</div>';

    // Echo / Fizzle
    html += '<div class="gd-metrics">';
    html += '<div class="gd-metric"><span class="gd-metric-val" style="color:#4ecb8d">' + node.echoCount + '</span><span class="gd-metric-lbl">Echoes</span></div>';
    html += '<div class="gd-metric"><span class="gd-metric-val" style="color:#f06b6b">' + node.fizzleCount + '</span><span class="gd-metric-lbl">Fizzles</span></div>';
    html += '<div class="gd-metric"><span class="gd-metric-val">' + age + 'd</span><span class="gd-metric-lbl">Age</span></div>';
    html += '</div>';

    // Date
    html += '<div class="gd-section"><span class="gd-section-lbl">Created</span><span>' + dateStr + '</span></div>';

    // Tags
    if (node.tags.length > 0) {
      html += '<div class="gd-section"><span class="gd-section-lbl">Tags</span><div class="gd-tags">';
      node.tags.forEach(function (t) { html += '<span class="tag">' + escHtml(t) + '</span>'; });
      html += '</div></div>';
    }

    // Entities
    if (node.entities.length > 0) {
      html += '<div class="gd-section"><span class="gd-section-lbl">Entities</span><div class="gd-tags">';
      node.entities.forEach(function (e) { html += '<span class="tag gd-entity-tag">' + escHtml(e) + '</span>'; });
      html += '</div></div>';
    }

    // Connections
    if (neighborNodes.length > 0) {
      html += '<div class="gd-section"><span class="gd-section-lbl">Connections (' + neighborNodes.length + ')</span>';
      html += '<div class="gd-connections">';
      neighborNodes.forEach(function (nn) {
        var nc = typeColors[nn.type] || '#5a5e72';
        var edge = g.edgeByPair[node.id + '|' + nn.id] || g.edgeByPair[nn.id + '|' + node.id];
        var rel = edge ? edge.relation : '';
        html += '<div class="gd-conn-item" data-node-id="' + escAttr(nn.id) + '">';
        html += '<span class="gd-conn-dot" style="background:' + nc + '"></span>';
        html += '<span class="gd-conn-label">' + escHtml(truncate(nn.label, 35)) + '</span>';
        if (rel) html += '<span class="gd-conn-rel">' + escHtml(rel) + '</span>';
        html += '</div>';
      });
      html += '</div></div>';
    }

    // ID
    html += '<div class="gd-section gd-id"><span class="gd-section-lbl">ID</span><span>' + escHtml(node.id) + '</span></div>';

    panel.innerHTML = html;

    // Click on connection item to select that node
    panel.querySelectorAll('.gd-conn-item').forEach(function (item) {
      item.addEventListener('click', function () {
        var targetId = this.dataset.nodeId;
        var targetNode = g.nodeMap[targetId];
        if (targetNode) {
          g.selectedNode = targetNode;
          showGraphDetail(targetNode);
          g.alpha = Math.max(g.alpha, 0.02);
          requestAnimationFrame(graphTick);
        }
      });
    });
  }

  function colorWithAlpha(hex, a) {
    // Convert hex like #5b9cf6 to rgba
    var r = parseInt(hex.slice(1, 3), 16);
    var g = parseInt(hex.slice(3, 5), 16);
    var b = parseInt(hex.slice(5, 7), 16);
    return 'rgba(' + r + ',' + g + ',' + b + ',' + a + ')';
  }

  function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y);
    ctx.quadraticCurveTo(x + w, y, x + w, y + r);
    ctx.lineTo(x + w, y + h - r);
    ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
    ctx.lineTo(x + r, y + h);
    ctx.quadraticCurveTo(x, y + h, x, y + h - r);
    ctx.lineTo(x, y + r);
    ctx.quadraticCurveTo(x, y, x + r, y);
    ctx.closePath();
  }

  function daysSince(isoDate) {
    try {
      var d = new Date(isoDate);
      var now = new Date();
      return Math.floor((now - d) / (1000 * 60 * 60 * 24));
    } catch (_) { return 0; }
  }

  // ─── Portal ──────────────────────────────────────────────────────

  var portalTooltipCache = {};

  async function loadPortal() {
    if (!currentRepo) return;
    var body = document.getElementById('portalBody');
    var toc = document.getElementById('portalToc');
    body.innerHTML = '<div class="loading">Loading...</div>';
    toc.innerHTML = '';

    try {
      var doc = await api('/api/eidet/portal?repo=' + encRepo());
      renderPortal(doc);
      applyProvenanceState();
    } catch (e) {
      body.innerHTML = '<div class="detail-placeholder">Could not load Portal: ' + escHtml(e.message || '') + '</div>';
    }
  }

  function renderPortal(doc) {
    var body = document.getElementById('portalBody');
    var toc = document.getElementById('portalToc');

    var bodyHtml = '';
    var tocHtml = '<h4>On this page</h4><ul>';
    doc.sections.forEach(function (s) {
      tocHtml += '<li><a href="#portal/' + encodeURIComponent(currentRepo) + '" data-portal-anchor="' +
        escHtml(s.id) + '">' + escHtml(s.title) + '</a></li>';
      bodyHtml += '<section class="portal-section" id="portal-section-' + escHtml(s.id) + '">';
      bodyHtml += '<h3>' + escHtml(s.title) + '</h3>';
      bodyHtml += s.html; // server-rendered, server-escaped
      bodyHtml += '</section>';
    });
    tocHtml += '</ul>';
    body.innerHTML = bodyHtml;
    toc.innerHTML = tocHtml;

    // TOC scroll-to behaviour without leaving the route.
    toc.querySelectorAll('[data-portal-anchor]').forEach(function (a) {
      a.addEventListener('click', function (ev) {
        ev.preventDefault();
        var target = document.getElementById('portal-section-' + this.dataset.portalAnchor);
        if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      });
    });

    // Citation hover tooltips fetched fresh per spec.
    body.querySelectorAll('a.portal-cite').forEach(function (a) {
      a.addEventListener('mouseenter', function () { showPortalTooltip(a); });
      a.addEventListener('mouseleave', hidePortalTooltip);
      a.addEventListener('focus', function () { showPortalTooltip(a); });
      a.addEventListener('blur', hidePortalTooltip);
    });
  }

  async function showPortalTooltip(anchor) {
    var id = anchor.dataset.mid;
    if (!id) return;
    var tip = document.getElementById('portalTooltip');
    if (!tip) return;
    var rect = anchor.getBoundingClientRect();
    tip.style.left = (window.scrollX + rect.left) + 'px';
    tip.style.top = (window.scrollY + rect.bottom + 4) + 'px';
    tip.hidden = false;
    tip.innerHTML = '<div class="loading">Loading...</div>';

    try {
      var entry = portalTooltipCache[id];
      if (!entry) {
        entry = await api('/api/eidet/' + encodeURIComponent(id));
        portalTooltipCache[id] = entry;
      }
      tip.innerHTML =
        '<div class="tip-type ' + escHtml(entry.type || '') + '">' + escHtml(entry.type || '') + '</div>' +
        '<div class="tip-line">' + escHtml(entry.oneLiner || entry.summary || entry.content || '') + '</div>' +
        '<div class="tip-meta">importance ' + (entry.importance || 0).toFixed(2) +
        ' · created ' + escHtml((entry.createdAt || '').split('T')[0]) + '</div>';
    } catch (_) {
      tip.innerHTML = '<div class="tip-meta">Memory not available</div>';
    }
  }

  function hidePortalTooltip() {
    var tip = document.getElementById('portalTooltip');
    if (tip) tip.hidden = true;
  }

  function applyProvenanceState() {
    var stored = localStorage.getItem('eidet.portal.provenance');
    var on = stored === '1';
    var cb = document.getElementById('portalProvenanceToggle');
    if (cb) cb.checked = on;
    document.getElementById('page-portal').classList.toggle('show-provenance', on);
  }

  function setupPortalControls() {
    var cb = document.getElementById('portalProvenanceToggle');
    if (!cb) return;
    cb.addEventListener('change', function () {
      var on = this.checked;
      localStorage.setItem('eidet.portal.provenance', on ? '1' : '0');
      document.getElementById('page-portal').classList.toggle('show-provenance', on);
    });
  }

  // ─── Canon ─────────────────────────────────────────────────────────

  var currentCanonDraft = null; // the draft currently open in the detail panel

  // Canon's file-backed source (UL.md) needs the raw filesystem path, not the normalized id — pass the
  // original path when we know it; the RepoId is normalized server-side either way.
  function canonRepoParam() {
    return encodeURIComponent(repoPathMap[currentRepo] || currentRepo);
  }

  function setupCanonListeners() {
    document.getElementById('canonRegenerate').addEventListener('click', regenerateCanonDrafts);
    document.getElementById('canonBulkApproveUl').addEventListener('click', function () {
      bulkApproveCanon('ubiquitous-language');
    });
  }

  async function loadCanon() {
    if (!currentRepo) return;
    var list = document.getElementById('canonDraftList');
    list.innerHTML = '<div class="loading">Loading...</div>';
    document.getElementById('canonActionResult').textContent = '';

    try {
      var data = await api('/api/eidet/canon/drafts?repo=' + canonRepoParam() + '&limit=100');
      var drafts = data.drafts || [];
      if (drafts.length === 0) {
        list.innerHTML = '<div class="empty-state">No pending Canon drafts. Try "Regenerate drafts".</div>';
        return;
      }
      list.innerHTML = drafts.map(canonDraftItemHtml).join('');
      list.querySelectorAll('.canon-draft-item').forEach(function (item) {
        item.addEventListener('click', function () {
          list.querySelectorAll('.canon-draft-item').forEach(function (i) { i.classList.remove('selected'); });
          this.classList.add('selected');
          showCanonDraft(this.dataset.id);
        });
      });
    } catch (_) {
      list.innerHTML = '<div class="empty-state">Error loading Canon drafts</div>';
    }
  }

  // Draft summaries are server-generated but Title/Slug originate in untrusted member content — escape all.
  function canonDraftItemHtml(d) {
    var badges = '<span class="memory-type insight">' + escHtml(d.kind) + '</span>';
    if (d.replacesExisting) badges += '<span class="canon-badge">supersedes</span>';
    var meta = d.memberCount + ' citation' + (d.memberCount === 1 ? '' : 's');
    return '<div class="memory-item canon-draft-item" data-id="' + escAttr(d.id) + '">' +
      '<div class="memory-header">' + badges +
      '<span class="memory-date">' + formatDate(d.proposedAt) + '</span></div>' +
      '<div class="memory-content">' + escHtml(d.title) + '</div>' +
      '<div class="memory-tags"><span class="tag">' + escHtml(d.slug) + '</span>' +
      '<span class="canon-meta">' + escHtml(meta) + '</span></div>' +
      '</div>';
  }

  async function showCanonDraft(id) {
    var panel = document.getElementById('canonDetailPanel');
    panel.innerHTML = '<div class="loading">Loading...</div>';
    try {
      var detail = await api('/api/eidet/canon/drafts/' + encodeURIComponent(id));
      currentCanonDraft = detail;
      renderCanonDetail(detail);
    } catch (_) {
      panel.innerHTML = '<div class="detail-placeholder">Could not load draft</div>';
    }
  }

  // Render-trust gate: draft prose and citation text are untrusted content — every field is escaped
  // (textContent for the editor, escHtml for prose/citations). Citations link into the memory route.
  function renderCanonDetail(detail) {
    var panel = document.getElementById('canonDetailPanel');
    var head = detail.head;

    var html = '<div class="detail-actions">';
    html += '<button class="btn btn-success" onclick="window.__canonApprove()">Approve</button>';
    html += '<button class="btn btn-warning" onclick="window.__canonReject()">Reject</button>';
    html += '</div>';

    html += '<div class="detail-section"><label>Title</label><div class="value">' + escHtml(head.title) + '</div></div>';
    html += '<div class="detail-section"><label>Tag</label><span class="tag">' +
      escHtml('canon:' + head.kind.toLowerCase() + ':' + head.slug) + '</span></div>';

    html += '<div class="detail-section"><label>Proposed content (editable)</label>' +
      '<textarea id="canonEditContent" class="form-control form-textarea" rows="8"></textarea></div>';

    html += '<div class="detail-section"><label>Citations (' + detail.citations.length + ')</label>';
    if (detail.citations.length === 0) {
      html += '<div class="value canon-empty">No cited memories (authored term).</div>';
    } else {
      html += '<ul class="canon-citations">';
      detail.citations.forEach(function (c) {
        html += '<li><a href="' + escAttr(c.href) + '">' +
          '<span class="memory-type ' + escAttr(String(c.type).toLowerCase()) + '">' + escHtml(c.type) + '</span> ' +
          escHtml(c.oneLiner) + '</a></li>';
      });
      html += '</ul>';
    }
    html += '</div>';

    html += '<div id="canonDetailResult" class="form-result"></div>';
    panel.innerHTML = html;
    // Set the editor value via textContent (never innerHTML) so proposed prose can't inject markup.
    document.getElementById('canonEditContent').value = detail.proposedContent;
  }

  window.__canonApprove = async function () {
    if (!currentCanonDraft) return;
    var result = document.getElementById('canonDetailResult');
    var edited = document.getElementById('canonEditContent').value;
    result.textContent = 'Approving...';
    try {
      var res = await fetch(API + '/api/eidet/canon/drafts/' + encodeURIComponent(currentCanonDraft.head.id) + '/approve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ editedContent: edited }),
      });
      var body = await res.json();
      if (res.ok && body.success) {
        result.textContent = 'Approved → ' + (body.mintedMemoryId || 'memory minted');
        currentCanonDraft = null;
        loadCanon();
        document.getElementById('canonDetailPanel').innerHTML = '<div class="detail-placeholder">Select a draft to review</div>';
      } else {
        result.textContent = 'Approve failed: ' + (body.reason || res.status);
      }
    } catch (e) {
      result.textContent = 'Error: ' + e.message;
    }
  };

  window.__canonReject = async function () {
    if (!currentCanonDraft) return;
    var reason = prompt('Reject reason:');
    if (!reason) return;
    var result = document.getElementById('canonDetailResult');
    result.textContent = 'Rejecting...';
    try {
      var res = await fetch(API + '/api/eidet/canon/drafts/' + encodeURIComponent(currentCanonDraft.head.id) + '/reject', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason: reason }),
      });
      var body = await res.json();
      if (res.ok && body.success) {
        currentCanonDraft = null;
        loadCanon();
        document.getElementById('canonDetailPanel').innerHTML = '<div class="detail-placeholder">Draft rejected</div>';
      } else {
        result.textContent = 'Reject failed: ' + (body.reason || res.status);
      }
    } catch (e) {
      result.textContent = 'Error: ' + e.message;
    }
  };

  async function regenerateCanonDrafts() {
    if (!currentRepo) return;
    var result = document.getElementById('canonActionResult');
    result.textContent = 'Regenerating...';
    try {
      var data = await apiPost('/api/eidet/canon/regenerate?repo=' + canonRepoParam());
      result.textContent = (data.drafts || 0) + ' draft(s) created or refreshed';
      loadCanon();
    } catch (e) {
      result.textContent = 'Error: ' + e.message;
    }
  }

  async function bulkApproveCanon(source) {
    if (!currentRepo) return;
    var result = document.getElementById('canonActionResult');
    result.textContent = 'Approving ' + source + ' drafts...';
    try {
      var res = await fetch(API + '/api/eidet/canon/bulk-approve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ repo: repoPathMap[currentRepo] || currentRepo, source: source }),
      });
      var body = await res.json();
      if (res.ok) {
        result.textContent = 'Approved ' + (body.approved || 0) + ', failed ' + (body.failed || 0);
        loadCanon();
      } else {
        result.textContent = 'Bulk approve failed: ' + (body.error || res.status);
      }
    } catch (e) {
      result.textContent = 'Error: ' + e.message;
    }
  }

  // ─── Timeline ────────────────────────────────────────────────────

  async function loadTimeline() {
    if (!currentRepo) return;
    var container = document.getElementById('timelineContainer');
    container.innerHTML = '<div class="loading">Loading timeline...</div>';

    try {
      var data = await api('/api/eidet/browse?repo=' + encRepo() + '&skip=0&take=100');
      var entries = data.entries;

      if (entries.length === 0) {
        container.innerHTML = '<div class="empty-state">No memories to display</div>';
        return;
      }

      // Group by date
      var groups = {};
      entries.forEach(function (e) {
        var d = e.createdAt ? e.createdAt.split('T')[0] : 'Unknown';
        if (!groups[d]) groups[d] = [];
        groups[d].push(e);
      });

      var html = '';
      Object.keys(groups).sort().reverse().forEach(function (date) {
        html += '<div class="timeline-group">';
        html += '<div class="timeline-date">' + date + '</div>';
        groups[date].forEach(function (e) {
          html += '<div class="timeline-item">';
          html += '<div class="memory-header">';
          html += '<span class="memory-type ' + e.type + '">' + e.type + '</span>';
          if (e.tags && e.tags.length > 0) {
            html += e.tags.slice(0, 3).map(function (t) { return '<span class="tag">' + escHtml(t) + '</span>'; }).join('');
          }
          html += '</div>';
          html += '<div class="memory-content">' + escHtml(e.oneLiner || e.summary || truncate(e.content, 120)) + '</div>';
          html += '</div>';
        });
        html += '</div>';
      });

      container.innerHTML = html;
    } catch (_) {
      container.innerHTML = '<div class="empty-state">Could not load timeline</div>';
    }
  }

  // ─── Usage Analytics ──────────────────────────────────────────────

  async function loadUsage() {
    if (!currentRepo) return;
    var summary = document.getElementById('usageSummary');
    var table = document.getElementById('usageTable');
    summary.innerHTML = '<div class="loading">Loading usage data...</div>';
    table.innerHTML = '';

    var days = parseInt(document.getElementById('usageDays').value) || 7;

    try {
      var data = await api('/api/eidet/usage?repo=' + encRepo() + '&days=' + days);

      if (data.totalCalls === 0) {
        summary.innerHTML = '<div class="empty-state">No usage data recorded yet for this period.</div>';
        table.innerHTML = '';
        clearUsageChart();
        return;
      }

      // Summary cards
      var avgDuration = data.operations.length > 0
        ? (data.operations.reduce(function (sum, o) { return sum + o.totalDurationMs; }, 0) / data.totalCalls)
        : 0;

      summary.innerHTML =
        statCard('Total Calls', data.totalCalls, '') +
        statCard('Operations', data.operations.length, '') +
        statCard('Avg Duration', avgDuration.toFixed(0) + 'ms', '') +
        statCard('Period', days + 'd', '');

      // Operations table
      if (data.operations.length > 0) {
        var ops = data.operations.slice().sort(function (a, b) { return b.callCount - a.callCount; });
        var html = '<table class="usage-table">';
        html += '<thead><tr><th>Operation</th><th>Calls</th><th>Avg (ms)</th><th>Min (ms)</th><th>Max (ms)</th><th>Results</th><th>Last Call</th></tr></thead>';
        html += '<tbody>';
        ops.forEach(function (op) {
          html += '<tr>';
          html += '<td><span class="usage-op">' + escHtml(op.operation) + '</span></td>';
          html += '<td class="num">' + op.callCount + '</td>';
          html += '<td class="num">' + op.avgDurationMs.toFixed(1) + '</td>';
          html += '<td class="num">' + op.minDurationMs.toFixed(1) + '</td>';
          html += '<td class="num">' + op.maxDurationMs.toFixed(1) + '</td>';
          html += '<td class="num">' + op.totalResults + '</td>';
          html += '<td class="date">' + formatDate(op.lastCall) + '</td>';
          html += '</tr>';
        });
        html += '</tbody></table>';
        table.innerHTML = html;
      }

      // Load hourly chart data
      loadUsageChart(days);
    } catch (e) {
      summary.innerHTML = '<div class="empty-state">Could not load usage data' +
        (e.message && e.message.includes('503') ? ' — usage tracking not available' : '') + '</div>';
    }
  }

  async function loadUsageChart(days) {
    try {
      var data = await api('/api/eidet/usage/hourly?repo=' + encRepo() + '&days=' + days);
      renderUsageChart(data.buckets || []);
    } catch (_) {
      clearUsageChart();
    }
  }

  function clearUsageChart() {
    var canvas = document.getElementById('usageChart');
    if (!canvas) return;
    var ctx = canvas.getContext('2d');
    var container = canvas.parentElement;
    canvas.width = container.clientWidth - 40;
    canvas.height = 200;
    ctx.fillStyle = '#22263a';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#5a5e72';
    ctx.textAlign = 'center';
    ctx.font = '13px -apple-system, system-ui, sans-serif';
    ctx.fillText('No activity data available', canvas.width / 2, canvas.height / 2);
  }

  function renderUsageChart(buckets) {
    var canvas = document.getElementById('usageChart');
    if (!canvas) return;
    var ctx = canvas.getContext('2d');
    var container = canvas.parentElement;
    canvas.width = container.clientWidth - 40;
    canvas.height = 200;
    var W = canvas.width;
    var H = canvas.height;
    var pad = { top: 20, right: 20, bottom: 40, left: 50 };
    var chartW = W - pad.left - pad.right;
    var chartH = H - pad.top - pad.bottom;

    ctx.fillStyle = '#22263a';
    ctx.fillRect(0, 0, W, H);

    if (buckets.length === 0) {
      ctx.fillStyle = '#5a5e72';
      ctx.textAlign = 'center';
      ctx.font = '13px -apple-system, system-ui, sans-serif';
      ctx.fillText('No activity data', W / 2, H / 2);
      return;
    }

    var maxCalls = Math.max.apply(null, buckets.map(function (b) { return b.totalCalls; }));
    if (maxCalls === 0) maxCalls = 1;

    var barW = Math.max(2, Math.floor(chartW / buckets.length) - 1);

    // Y-axis gridlines
    ctx.strokeStyle = 'rgba(90,94,114,0.3)';
    ctx.lineWidth = 1;
    ctx.font = '10px -apple-system, system-ui, sans-serif';
    ctx.fillStyle = '#5a5e72';
    ctx.textAlign = 'right';
    for (var i = 0; i <= 4; i++) {
      var yVal = Math.round(maxCalls * i / 4);
      var yPos = pad.top + chartH - (chartH * i / 4);
      ctx.beginPath();
      ctx.moveTo(pad.left, yPos);
      ctx.lineTo(W - pad.right, yPos);
      ctx.stroke();
      ctx.fillText(String(yVal), pad.left - 6, yPos + 3);
    }

    // Bars
    ctx.fillStyle = '#6c8cff';
    buckets.forEach(function (b, idx) {
      var x = pad.left + (idx * (chartW / buckets.length));
      var barH = (b.totalCalls / maxCalls) * chartH;
      ctx.fillStyle = 'rgba(108,140,255,0.8)';
      ctx.fillRect(x, pad.top + chartH - barH, barW, barH);
    });

    // X-axis labels (show a few evenly spaced)
    ctx.fillStyle = '#5a5e72';
    ctx.textAlign = 'center';
    ctx.font = '10px -apple-system, system-ui, sans-serif';
    var labelCount = Math.min(buckets.length, 8);
    var step = Math.max(1, Math.floor(buckets.length / labelCount));
    for (var j = 0; j < buckets.length; j += step) {
      var xLabel = pad.left + (j * (chartW / buckets.length)) + barW / 2;
      var d = new Date(buckets[j].hour);
      var label = (d.getMonth() + 1) + '/' + d.getDate() + ' ' + d.getHours() + ':00';
      ctx.fillText(label, xLabel, H - pad.bottom + 16);
    }
  }

  // ─── Settings ────────────────────────────────────────────────────

  async function loadSettings() {
    var list = document.getElementById('configList');
    list.innerHTML = '<div class="loading">Loading configuration...</div>';

    try {
      var data = await api('/api/status');
      var rows = '';
      // Show status info as config-like display
      rows += configRow('version', data.version);
      rows += configRow('status', data.status);
      rows += configRow('uptime', data.uptime);
      rows += configRow('api', data.api);
      if (data.database) {
        rows += configRow('database.name', data.database.name);
        rows += configRow('database.version', data.database.serverVersion);
        rows += configRow('database.documents', data.database.documentCount);
        rows += configRow('database.indexExists', data.database.indexExists);
      }
      list.innerHTML = rows;
    } catch (_) {
      list.innerHTML = '<div class="empty-state">Could not load settings</div>';
    }

    loadScheduledTasks();
    loadRepoLinks();
  }

  async function loadScheduledTasks() {
    var el = document.getElementById('scheduledTasksList');
    if (!el) return;
    el.innerHTML = '<div class="loading">Loading scheduled tasks...</div>';

    try {
      var data = await api('/api/eidet/scheduled-tasks');
      if (!data.tasks || data.tasks.length === 0) {
        el.innerHTML = '<div class="empty-state">No scheduled tasks</div>';
        return;
      }

      var html = '';
      data.tasks.forEach(function (t) {
        var statusClass = 'st-' + (t.status || 'pending');
        var statusLabel = t.status || 'pending';
        var nextRun = t.nextRunAt ? formatRelativeTime(t.nextRunAt) : '--';
        var lastRun = t.lastRunAt ? formatRelativeTime(t.lastRunAt) : 'never';
        var lastDuration = t.lastDurationMs != null ? (t.lastDurationMs / 1000).toFixed(1) + 's' : '--';

        html += '<div class="st-card">';
        html += '<div class="st-header">';
        html += '<span class="st-name">' + escHtml(t.taskType) + '</span>';
        html += '<span class="st-status ' + statusClass + '">' + escHtml(statusLabel) + '</span>';
        html += '</div>';
        html += '<div class="st-details">';
        html += '<div class="st-detail"><span class="st-label">Interval</span><span>' + t.intervalHours + 'h</span></div>';
        html += '<div class="st-detail"><span class="st-label">Next run</span><span>' + escHtml(nextRun) + '</span></div>';
        html += '<div class="st-detail"><span class="st-label">Last run</span><span>' + escHtml(lastRun) + '</span></div>';
        html += '<div class="st-detail"><span class="st-label">Duration</span><span>' + escHtml(lastDuration) + '</span></div>';
        html += '<div class="st-detail"><span class="st-label">Runs</span><span>' + t.runCount + '</span></div>';
        html += '<div class="st-detail"><span class="st-label">Errors</span><span class="' + (t.errorCount > 0 ? 'st-error-count' : '') + '">' + t.errorCount + '</span></div>';
        html += '</div>';
        if (t.lastError) {
          html += '<div class="st-error">' + escHtml(t.lastError) + '</div>';
        }
        html += '</div>';
      });

      el.innerHTML = html;
    } catch (_) {
      el.innerHTML = '<div class="empty-state">Could not load scheduled tasks</div>';
    }
  }

  async function loadRepoLinks() {
    var listEl = document.getElementById('repoLinksList');
    var selectEl = document.getElementById('linkTargetRepo');
    if (!listEl || !selectEl) return;

    // Populate target repo dropdown (exclude current repo)
    selectEl.innerHTML = '';
    try {
      var data = await api('/api/eidet/repos');
      data.repos.forEach(function (r) {
        if (r.repoId !== currentRepo) {
          var opt = document.createElement('option');
          opt.value = r.repoId;
          opt.textContent = formatRepoDisplay(r.originalPath || r.repoId);
          opt.title = r.originalPath || r.repoId;
          selectEl.appendChild(opt);
        }
      });
    } catch (_) {}

    // Load existing links
    if (!currentRepo) { listEl.innerHTML = ''; return; }
    try {
      var linksData = await api('/api/eidet/links?repo=' + encRepo());
      if (!linksData.links || linksData.links.length === 0) {
        listEl.innerHTML = '<div style="font-size:12px;color:var(--text-muted)">No repo links yet.</div>';
        return;
      }
      var html = '';
      linksData.links.forEach(function (l) {
        var tags = l.tags || [];
        var relation = tags.find(function (t) { return t !== 'cross-repo-link'; }) || 'related';
        var target = (l.content || '').replace('Cross-repo link: ' + relation + ' -> ', '');
        html += '<div class="link-item">';
        html += '<span class="link-relation">' + escHtml(relation) + '</span>';
        html += '<span class="link-target">' + escHtml(target) + '</span>';
        html += '<span class="link-remove" title="Forget this link" onclick="window.__eidetForgetLink(\'' + escAttr(l.id) + '\')">&times;</span>';
        html += '</div>';
      });
      listEl.innerHTML = html;
    } catch (_) {
      listEl.innerHTML = '<div style="font-size:12px;color:var(--text-muted)">Could not load links.</div>';
    }
  }

  async function createRepoLink() {
    var resultEl = document.getElementById('repoLinkResult');
    var targetRepo = document.getElementById('linkTargetRepo').value;
    var relation = document.getElementById('linkRelation').value;
    if (!targetRepo || !currentRepo) {
      resultEl.className = 'form-result error';
      resultEl.textContent = 'Select a target repository.';
      return;
    }

    resultEl.className = 'form-result';
    resultEl.textContent = 'Creating link...';

    try {
      var res = await fetch(API + '/api/eidet/links', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          repo: repoPathMap[currentRepo] || currentRepo,
          targetRepo: repoPathMap[targetRepo] || targetRepo,
          relation: relation,
        }),
      });
      var data = await res.json();
      if (res.ok) {
        resultEl.className = 'form-result success';
        resultEl.textContent = 'Link created: ' + relation + ' -> ' + targetRepo;
        loadRepoLinks();
      } else {
        resultEl.className = 'form-result error';
        resultEl.textContent = data.error || 'Failed to create link';
      }
    } catch (e) {
      resultEl.className = 'form-result error';
      resultEl.textContent = 'Error: ' + e.message;
    }
  }

  window.__eidetForgetLink = async function (linkId) {
    if (!confirm('Remove this repo link?')) return;
    try {
      await fetch(API + '/api/eidet/' + encodeURIComponent(linkId), { method: 'DELETE' });
      loadRepoLinks();
    } catch (_) {}
  };

  function formatRelativeTime(isoDate) {
    if (!isoDate) return '--';
    try {
      var d = new Date(isoDate);
      var now = new Date();
      var diffMs = d - now;
      var diffSec = Math.abs(diffMs) / 1000;
      var past = diffMs < 0;

      if (diffSec < 60) return past ? 'just now' : 'in <1m';
      if (diffSec < 3600) {
        var mins = Math.floor(diffSec / 60);
        return past ? mins + 'm ago' : 'in ' + mins + 'm';
      }
      if (diffSec < 86400) {
        var hrs = Math.floor(diffSec / 3600);
        return past ? hrs + 'h ago' : 'in ' + hrs + 'h';
      }
      var days = Math.floor(diffSec / 86400);
      return past ? days + 'd ago' : 'in ' + days + 'd';
    } catch (_) {
      return formatDate(isoDate);
    }
  }

  function configRow(key, value) {
    return '<div class="config-row"><span class="config-key">' + escHtml(key) +
      '</span><span class="config-value">' + escHtml(String(value)) + '</span></div>';
  }

  // Maintenance answers 200 with the report when it finishes inside the service's grace window and
  // 202 with a run id when it does not. A long run is not a failure, so follow it to the end.
  async function runMaintenance(output) {
    var res = await fetch('/api/maintenance?repo=' + encRepo(), { method: 'POST' });
    if (!res.ok) throw new Error('API error: ' + res.status);
    var body = await res.json();
    if (res.status !== 202) return body;

    var started = Date.now();
    for (;;) {
      output.textContent = 'Maintenance still running (' +
        Math.round((Date.now() - started) / 1000) + 's)...';
      await new Promise(function (done) { setTimeout(done, 5000); });
      var run = await api(body.poll);
      if (run.status === 'running') continue;
      if (run.status === 'failed') throw new Error(run.error);
      return run.report;
    }
  }

  async function runAction(action) {
    var result = document.getElementById('actionResult');
    if (!currentRepo) { result.textContent = 'No repo selected'; return; }
    result.textContent = 'Running ' + action + '...';

    try {
      var data;
      if (action === 'export') {
        var res = await fetch('/api/eidet/export?repo=' + encRepo());
        data = await res.text();
      } else if (action === 'intake') {
        var intakeUrl = '/api/eidet/intake?repo=' + encRepo();
        if (repoPathMap[currentRepo]) intakeUrl += '&path=' + encodeURIComponent(repoPathMap[currentRepo]);
        data = await apiPost(intakeUrl);
      } else if (action === 'consolidate') {
        data = await apiPost('/api/eidet/consolidate?repo=' + encRepo());
      } else if (action === 'maintenance') {
        data = await runMaintenance(result);
      }
      result.textContent = typeof data === 'string' ? data : JSON.stringify(data, null, 2);
    } catch (e) {
      result.textContent = 'Error: ' + e.message;
    }
  }

  // ─── Shared HTML templates ───────────────────────────────────────

  function memoryItemHtml(entry) {
    var type = entry.type || 'observation';
    var content = entry.oneLiner || entry.summary || truncate(entry.content, 100);
    var date = entry.createdAt ? formatDate(entry.createdAt) : '';
    var tags = (entry.tags || []).slice(0, 4).map(function (t) {
      return '<span class="tag">' + escHtml(t) + '</span>';
    }).join('');

    return '<div class="memory-item" data-id="' + escAttr(entry.id) + '">' +
      '<div class="memory-header">' +
      '<span class="memory-type ' + type + '">' + type + '</span>' +
      '<span class="memory-date">' + date + '</span>' +
      '</div>' +
      '<div class="memory-content">' + escHtml(content) + '</div>' +
      (tags ? '<div class="memory-tags">' + tags + '</div>' : '') +
      '</div>';
  }

  // ─── Utilities ───────────────────────────────────────────────────

  function escHtml(s) {
    if (!s) return '';
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function escAttr(s) {
    return escHtml(s).replace(/'/g, '&#39;');
  }

  function truncate(s, len) {
    if (!s) return '';
    return s.length > len ? s.substring(0, len) + '...' : s;
  }

  // Smart display for repo paths:
  //   P:\Eidet            → P:\Eidet
  //   C:\Users\steve\Projects\MyApp → MyApp  (C:\...\Projects)
  function formatRepoDisplay(pathOrId) {
    if (!pathOrId) return '';
    var sep = pathOrId.indexOf('\\') >= 0 ? '\\' : '/';
    var parts = pathOrId.split(sep).filter(Boolean);
    // Normalized IDs use dashes — show as-is
    if (parts.length <= 1) return pathOrId;
    var name = parts[parts.length - 1];
    // Short paths (drive + folder): show full
    if (parts.length <= 2) return pathOrId;
    // Deep paths: show "Name  (drive:\...\parent)"
    var drive = parts[0];
    var parent = parts[parts.length - 2];
    return name + '  (' + drive + sep + '...' + sep + parent + ')';
  }

  function formatDate(iso) {
    if (!iso) return '';
    try {
      var d = new Date(iso);
      return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    } catch (_) {
      return iso.split('T')[0];
    }
  }
})();
