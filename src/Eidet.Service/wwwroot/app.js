// Eidet Memory Explorer — Single Page Application
(function () {
  'use strict';

  const API = '';
  let currentRepo = '';
  let browseSkip = 0;
  const browseTake = 30;

  // ─── Init ────────────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', init);

  async function init() {
    setupNavigation();
    setupEventListeners();
    await loadServiceInfo();
    await loadRepos();
    navigateToHash();
  }

  // ─── Navigation ──────────────────────────────────────────────────

  function setupNavigation() {
    window.addEventListener('hashchange', navigateToHash);
  }

  function navigateToHash() {
    var hash = location.hash.slice(1) || 'dashboard';
    showPage(hash);
  }

  function showPage(name) {
    document.querySelectorAll('.page').forEach(function (p) { p.classList.remove('active'); });
    document.querySelectorAll('.nav-link').forEach(function (l) { l.classList.remove('active'); });
    var page = document.getElementById('page-' + name);
    var link = document.querySelector('[data-page="' + name + '"]');
    if (page) page.classList.add('active');
    if (link) link.classList.add('active');
    if (name === 'dashboard') loadDashboard();
    else if (name === 'browser') loadBrowser();
    else if (name === 'graph') loadGraph();
    else if (name === 'timeline') loadTimeline();
    else if (name === 'settings') loadSettings();
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

    document.getElementById('btnIntake').addEventListener('click', function () { runAction('intake'); });
    document.getElementById('btnConsolidate').addEventListener('click', function () { runAction('consolidate'); });
    document.getElementById('btnMaintenance').addEventListener('click', function () { runAction('maintenance'); });
    document.getElementById('btnExport').addEventListener('click', function () { runAction('export'); });
  }

  // ─── API helpers ─────────────────────────────────────────────────

  async function api(path) {
    var res = await fetch(API + path);
    if (!res.ok) throw new Error('API error: ' + res.status);
    return res.json();
  }

  async function apiPost(path) {
    var res = await fetch(API + path, { method: 'POST' });
    if (!res.ok) throw new Error('API error: ' + res.status);
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
      data.repos.forEach(function (r) {
        var opt = document.createElement('option');
        opt.value = r.repoId;
        // Show short name for display
        var parts = r.repoId.replace(/\\/g, '/').split('/');
        opt.textContent = parts[parts.length - 1] || r.repoId;
        opt.title = r.repoId;
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
      var data = await api('/api/eidet/browse?repo=' + encRepo() + '&skip=0&take=100');
      var entries = data.entries;

      // Count by type
      var counts = { observation: 0, insight: 0, procedure: 0, heuristic: 0 };
      entries.forEach(function (e) { counts[e.type] = (counts[e.type] || 0) + 1; });

      grid.innerHTML =
        statCard('Observations', counts.observation, 'observation') +
        statCard('Insights', counts.insight, 'insight') +
        statCard('Procedures', counts.procedure, 'procedure') +
        statCard('Heuristics', counts.heuristic, 'heuristic') +
        statCard('Total', entries.length, '');

      // Recent
      var recent = entries.slice(0, 10);
      list.innerHTML = recent.length > 0
        ? recent.map(memoryItemHtml).join('')
        : '<div class="empty-state">No memories yet</div>';

      setupMemoryClicks(list);
    } catch (_) {
      grid.innerHTML = '<div class="empty-state">Could not load dashboard</div>';
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
      panel.innerHTML =
        '<div class="detail-section"><label>Type</label>' +
        '<span class="memory-type ' + entry.type + '">' + entry.type + '</span></div>' +
        '<div class="detail-section"><label>Content</label><div class="value">' + escHtml(entry.content) + '</div></div>' +
        (entry.oneLiner ? '<div class="detail-section"><label>One-liner</label><div class="value">' + escHtml(entry.oneLiner) + '</div></div>' : '') +
        (entry.summary ? '<div class="detail-section"><label>Summary</label><div class="value">' + escHtml(entry.summary) + '</div></div>' : '') +
        (entry.foresightHint ? '<div class="detail-section"><label>Foresight</label><div class="value">' + escHtml(entry.foresightHint) + '</div></div>' : '') +
        '<div class="detail-section"><label>Tags</label><div class="memory-tags">' +
        (entry.tags || []).map(function (t) { return '<span class="tag">' + escHtml(t) + '</span>'; }).join('') +
        '</div></div>' +
        '<div class="detail-section"><label>Entities</label><div class="value">' +
        (entry.entities || []).map(function (e) { return escHtml(e); }).join(', ') +
        '</div></div>' +
        '<div class="detail-meta">' +
        metaItem('Importance', (entry.importance * 100).toFixed(0) + '%') +
        metaItem('Confidence', (entry.confidence * 100).toFixed(0) + '%') +
        metaItem('Accessed', entry.accessCount + 'x') +
        metaItem('Echoes', entry.echoCount + ' / Fizzles: ' + entry.fizzleCount) +
        metaItem('Created', formatDate(entry.createdAt)) +
        metaItem('Provenance', entry.provenance || '--') +
        metaItem('Source', entry.source || '--') +
        metaItem('ID', '<span style="font-size:10px;word-break:break-all">' + escHtml(entry.id) + '</span>') +
        '</div>';
    } catch (_) {
      panel.innerHTML = '<div class="detail-placeholder">Could not load memory details</div>';
    }
  }

  function metaItem(label, value) {
    return '<div class="detail-section"><label>' + label + '</label><div class="value">' + value + '</div></div>';
  }

  // ─── Graph ───────────────────────────────────────────────────────

  var graphSim = null;

  async function loadGraph() {
    if (!currentRepo) return;
    var canvas = document.getElementById('graphCanvas');
    var ctx = canvas.getContext('2d');
    var container = canvas.parentElement;
    canvas.width = container.clientWidth;
    canvas.height = 500;

    ctx.fillStyle = '#22263a';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#5a5e72';
    ctx.textAlign = 'center';
    ctx.fillText('Loading graph...', canvas.width / 2, canvas.height / 2);

    try {
      var limit = parseInt(document.getElementById('graphLimit').value) || 100;
      var data = await api('/api/eidet/graph?repo=' + encRepo() + '&limit=' + limit);
      runForceGraph(canvas, data);
    } catch (_) {
      ctx.fillStyle = '#22263a';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#5a5e72';
      ctx.fillText('Could not load graph data', canvas.width / 2, canvas.height / 2);
    }
  }

  var typeColors = {
    observation: '#5b9cf6',
    insight: '#a87cff',
    procedure: '#4ecb8d',
    heuristic: '#f0a54a'
  };

  function runForceGraph(canvas, data) {
    var ctx = canvas.getContext('2d');
    var W = canvas.width;
    var H = canvas.height;
    var nodes = data.nodes.map(function (n, i) {
      return {
        id: n.id, type: n.type, label: n.label, importance: n.importance,
        x: W / 2 + (Math.random() - 0.5) * W * 0.6,
        y: H / 2 + (Math.random() - 0.5) * H * 0.6,
        vx: 0, vy: 0
      };
    });
    var nodeMap = {};
    nodes.forEach(function (n) { nodeMap[n.id] = n; });
    var edges = data.edges.filter(function (e) { return nodeMap[e.from] && nodeMap[e.to]; });

    if (graphSim) cancelAnimationFrame(graphSim);

    var dragging = null;
    var mouseX = 0, mouseY = 0;
    var hoveredNode = null;

    canvas.onmousedown = function (e) {
      var rect = canvas.getBoundingClientRect();
      var x = e.clientX - rect.left;
      var y = e.clientY - rect.top;
      for (var i = 0; i < nodes.length; i++) {
        var n = nodes[i];
        var dx = n.x - x, dy = n.y - y;
        if (dx * dx + dy * dy < 200) { dragging = n; break; }
      }
    };
    canvas.onmousemove = function (e) {
      var rect = canvas.getBoundingClientRect();
      mouseX = e.clientX - rect.left;
      mouseY = e.clientY - rect.top;
      if (dragging) { dragging.x = mouseX; dragging.y = mouseY; dragging.vx = 0; dragging.vy = 0; }
      hoveredNode = null;
      for (var i = 0; i < nodes.length; i++) {
        var n = nodes[i];
        var dx = n.x - mouseX, dy = n.y - mouseY;
        if (dx * dx + dy * dy < 200) { hoveredNode = n; break; }
      }
      canvas.style.cursor = hoveredNode ? 'pointer' : 'default';
    };
    canvas.onmouseup = function () { dragging = null; };

    var alpha = 1;
    function tick() {
      alpha *= 0.995;

      // Center gravity
      nodes.forEach(function (n) {
        n.vx += (W / 2 - n.x) * 0.0005;
        n.vy += (H / 2 - n.y) * 0.0005;
      });

      // Node repulsion
      for (var i = 0; i < nodes.length; i++) {
        for (var j = i + 1; j < nodes.length; j++) {
          var a = nodes[i], b = nodes[j];
          var dx = b.x - a.x;
          var dy = b.y - a.y;
          var dist = Math.sqrt(dx * dx + dy * dy) || 1;
          var force = -300 / (dist * dist);
          var fx = dx / dist * force * alpha;
          var fy = dy / dist * force * alpha;
          a.vx -= fx; a.vy -= fy;
          b.vx += fx; b.vy += fy;
        }
      }

      // Edge attraction
      edges.forEach(function (e) {
        var a = nodeMap[e.from], b = nodeMap[e.to];
        var dx = b.x - a.x;
        var dy = b.y - a.y;
        var dist = Math.sqrt(dx * dx + dy * dy) || 1;
        var force = (dist - 80) * 0.01 * alpha;
        var fx = dx / dist * force;
        var fy = dy / dist * force;
        a.vx += fx; a.vy += fy;
        b.vx -= fx; b.vy -= fy;
      });

      // Apply velocity
      nodes.forEach(function (n) {
        if (n === dragging) return;
        n.vx *= 0.85; n.vy *= 0.85;
        n.x += n.vx;
        n.y += n.vy;
        n.x = Math.max(20, Math.min(W - 20, n.x));
        n.y = Math.max(20, Math.min(H - 20, n.y));
      });

      // Draw
      ctx.fillStyle = '#22263a';
      ctx.fillRect(0, 0, W, H);

      // Edges
      ctx.strokeStyle = 'rgba(108,140,255,0.15)';
      ctx.lineWidth = 1;
      edges.forEach(function (e) {
        var a = nodeMap[e.from], b = nodeMap[e.to];
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
      });

      // Nodes
      nodes.forEach(function (n) {
        var r = 4 + n.importance * 8;
        var color = typeColors[n.type] || '#5a5e72';
        ctx.beginPath();
        ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();

        if (n === hoveredNode) {
          ctx.strokeStyle = '#fff';
          ctx.lineWidth = 2;
          ctx.stroke();
        }
      });

      // Tooltip for hovered node
      if (hoveredNode) {
        var text = hoveredNode.label;
        ctx.font = '12px -apple-system, system-ui, sans-serif';
        var tw = ctx.measureText(text).width;
        var tx = hoveredNode.x - tw / 2;
        var ty = hoveredNode.y - 20;
        ctx.fillStyle = 'rgba(15,17,23,0.9)';
        ctx.fillRect(tx - 6, ty - 14, tw + 12, 20);
        ctx.fillStyle = '#e4e6ed';
        ctx.textAlign = 'left';
        ctx.fillText(text, tx, ty);
        ctx.textAlign = 'center';
      }

      if (alpha > 0.01) {
        graphSim = requestAnimationFrame(tick);
      }
    }

    tick();
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
  }

  function configRow(key, value) {
    return '<div class="config-row"><span class="config-key">' + escHtml(key) +
      '</span><span class="config-value">' + escHtml(String(value)) + '</span></div>';
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
        data = await apiPost('/api/eidet/intake?repo=' + encRepo());
      } else if (action === 'consolidate') {
        data = await apiPost('/api/eidet/consolidate?repo=' + encRepo());
      } else if (action === 'maintenance') {
        data = await apiPost('/api/maintenance?repo=' + encRepo());
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
