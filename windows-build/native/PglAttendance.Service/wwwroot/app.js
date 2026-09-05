/*
 * Browser dashboard — a faithful mirror of MainForm.cs: same polling cadence,
 * same debounce, same wording, same live-event handling. Anything that differs
 * here would show up as the browser and the desktop disagreeing about what the
 * service is doing.
 */
(function () {
  'use strict';

  var LIMIT = 15;              // MainForm.Limit
  var POLL_MS = 4000;          // MainForm._poll
  var SEARCH_DEBOUNCE_MS = 350;// MainForm._searchDebounce
  var COALESCE_MS = 400;       // MainForm._refreshCoalesce
  var MAX_ACTIVITY = 50;

  var state = {
    page: 1,
    totalPages: 0,
    filter: 'all',
    search: '',
    stats: { total: 0, synced: 0, unsynced: 0 },
    settings: null
  };

  var el = {};
  var searchTimer = null;
  var coalesceTimer = null;
  var toastTimer = null;

  // ---------------------------------------------------------------- helpers

  function $(id) { return document.getElementById(id); }

  /* The service rejects state-changing calls without this header, which is what
     stops another site from driving the API with the admin's cookie. */
  function api(path, options) {
    var opts = options || {};
    opts.credentials = 'same-origin';
    opts.headers = opts.headers || {};
    opts.headers['X-Requested-With'] = 'PglAttendance';
    return fetch(path, opts).then(function (res) {
      if (res.status === 401) {
        window.location.href = '/login';
        throw new Error('unauthenticated');
      }
      return res;
    });
  }

  function json(path, options) {
    return api(path, options).then(function (res) {
      return res.json().then(function (body) {
        return { ok: res.ok, status: res.status, body: body };
      }).catch(function () {
        return { ok: res.ok, status: res.status, body: null };
      });
    });
  }

  function text(node, value) { node.textContent = value; }

  function num(n) {
    return (typeof n === 'number' ? n : 0).toLocaleString('en-US');
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }

  function fmtDate(d) {
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
      ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  /* Mirrors MainForm.FormatDt: parse when possible, otherwise show as sent. */
  function fmtPunch(s) {
    if (!s) return '';
    var parsed = new Date(s.replace(' ', 'T'));
    return isNaN(parsed.getTime()) ? s : fmtDate(parsed);
  }

  function fmtReceived(s) {
    if (!s) return '';
    var parsed = new Date(s);
    return isNaN(parsed.getTime()) ? s : fmtDate(parsed);
  }

  function shortenUrl(u) {
    try { return new URL(u).host; } catch (e) { return u || ''; }
  }

  function toast(message) {
    text(el.toast, message);
    el.toast.hidden = false;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.toast.hidden = true; }, 3200);
  }

  // ------------------------------------------------------------------ data

  function refresh() {
    return Promise.all([
      json('/api/health'),
      json('/stats'),
      loadPage()
    ]).then(function (results) {
      applyHealth(results[0]);
      applyStats(results[1]);
    }).catch(function () {
      /* keep polling — matches the desktop, which swallows and retries */
    });
  }

  function applyHealth(res) {
    if (!res || !res.ok || !res.body) {
      el.statusChip.className = 'status-chip offline';
      text(el.statusText, 'Service offline — will retry');
      text(el.statTotal, '—');
      text(el.statSynced, '—');
      text(el.statUnsynced, '—');
      return;
    }
    el.statusChip.className = 'status-chip ok';
    text(el.statusText,
      'Listening on port ' + res.body.port + '   ·   HRMIS  ' + shortenUrl(res.body.hrmisUrl));
  }

  function applyStats(res) {
    if (!res || !res.ok || !res.body) return;
    state.stats = res.body;
    text(el.statTotal, num(res.body.total));
    text(el.statSynced, num(res.body.synced));
    text(el.statUnsynced, num(res.body.unsynced));
  }

  function loadPage() {
    var url = '/attendance?page=' + state.page + '&limit=' + LIMIT +
      '&filter=' + encodeURIComponent(state.filter);
    if (state.search) url += '&search=' + encodeURIComponent(state.search);

    return json(url).then(function (res) {
      if (!res.ok || !res.body) return;
      var p = res.body;
      state.totalPages = p.totalPages || 0;
      renderGrid(p.data || []);
      text(el.pageLabel,
        'Page ' + p.page + ' of ' + Math.max(1, p.totalPages) + '   ·   ' + num(p.total) + ' records');
      el.btnPrev.disabled = state.page <= 1;
      el.btnNext.disabled = state.page >= state.totalPages;
    });
  }

  function renderGrid(rows) {
    el.gridBody.replaceChildren();
    el.gridEmpty.hidden = rows.length > 0;

    rows.forEach(function (r) {
      var tr = document.createElement('tr');
      appendCell(tr, r.id);
      appendCell(tr, r.userId);
      appendCell(tr, fmtPunch(r.dateTime));
      appendCell(tr, r.status);
      appendCell(tr, r.verifyType);

      var syncCell = document.createElement('td');
      var pill = document.createElement('span');
      pill.className = 'pill ' + (r.isSynced ? 'synced' : 'pending');
      text(pill, r.isSynced ? 'Synced' : 'Pending');
      syncCell.appendChild(pill);
      if (!r.isSynced && r.lastError) syncCell.title = r.lastError;
      tr.appendChild(syncCell);

      appendCell(tr, fmtReceived(r.createdAt), 'cell-mono');
      el.gridBody.appendChild(tr);
    });
  }

  function appendCell(tr, value, className) {
    var td = document.createElement('td');
    if (className) td.className = className;
    text(td, value === null || value === undefined ? '' : String(value));
    tr.appendChild(td);
  }

  // -------------------------------------------------------------- activity

  function addActivity(kind, title, detail) {
    el.activityEmpty.hidden = true;

    var li = document.createElement('li');

    var dot = document.createElement('span');
    dot.className = 'activity-dot ' + kind;
    li.appendChild(dot);

    var body = document.createElement('div');
    body.className = 'activity-body';
    var t = document.createElement('div');
    t.className = 'activity-title';
    text(t, title);
    var d = document.createElement('div');
    d.className = 'activity-detail';
    text(d, detail);
    body.appendChild(t);
    body.appendChild(d);
    li.appendChild(body);

    var when = document.createElement('span');
    when.className = 'activity-time';
    var now = new Date();
    text(when, pad(now.getHours()) + ':' + pad(now.getMinutes()) + ':' + pad(now.getSeconds()));
    li.appendChild(when);

    el.activityList.insertBefore(li, el.activityList.firstChild);
    while (el.activityList.children.length > MAX_ACTIVITY) {
      el.activityList.removeChild(el.activityList.lastChild);
    }
  }

  function scheduleRefresh() {
    if (coalesceTimer) return;
    coalesceTimer = setTimeout(function () {
      coalesceTimer = null;
      refresh();
    }, COALESCE_MS);
  }

  function startEvents() {
    var source = new EventSource('/api/events', { withCredentials: true });

    source.addEventListener('newRecord', function (e) {
      try {
        var r = JSON.parse(e.data);
        addActivity('received', 'Punch from user ' + (r.userId || '?'), 'status ' + (r.status || '?'));
      } catch (err) { /* malformed payload — ignore, as the desktop does */ }
      scheduleRefresh();
    });

    source.addEventListener('syncUpdate', function (e) {
      try {
        var r = JSON.parse(e.data);
        if (r.isSynced) {
          addActivity('synced', 'Synced record #' + r.id, 'HRMIS accepted');
        } else {
          addActivity('failed', 'Failed to sync record #' + r.id, 'will retry');
        }
      } catch (err) { /* ignore */ }
      scheduleRefresh();
    });

    source.addEventListener('statsUpdate', scheduleRefresh);

    source.onerror = function () {
      // EventSource reconnects on its own; the poll keeps the UI truthful
      // meanwhile, exactly like the desktop's SSE retry loop.
    };
  }

  // --------------------------------------------------------------- actions

  function syncAll() {
    el.btnSyncAll.disabled = true;
    json('/sync-all', { method: 'POST' })
      .then(function (res) {
        var ok = res.ok && res.body && res.body.success;
        addActivity('info', ok ? 'Sync all triggered' : 'Sync all failed',
          ok ? 'queued unsynced records' : 'service not reachable');
        if (res.body && res.body.message) toast(res.body.message);
        return refresh();
      })
      .catch(function () {
        addActivity('info', 'Sync all failed', 'service not reachable');
      })
      .finally(function () { el.btnSyncAll.disabled = false; });
  }

  function askDeleteAll() {
    if (state.stats.total === 0) {
      toast('There are no records to delete.');
      return;
    }
    // Same wording as the desktop confirmation, including the pending warning.
    var msg = 'This permanently deletes all ' + num(state.stats.total) +
      ' attendance records from this computer’s local database.\n\n';
    msg += state.stats.unsynced > 0
      ? 'WARNING: ' + num(state.stats.unsynced) + ' records are still PENDING and have NOT been ' +
        'synced to HRMIS. Deleting now loses them forever.\n\n'
      : 'All records have already been synced to HRMIS.\n\n';
    msg += 'Delete everything?';
    text(el.confirmText, msg);
    el.confirmModal.hidden = false;
  }

  function deleteAll() {
    el.confirmModal.hidden = true;
    el.btnDeleteAll.disabled = true;
    json('/attendance', { method: 'DELETE' })
      .then(function (res) {
        if (!res.ok || !res.body) {
          addActivity('failed', 'Delete all failed', 'service not reachable');
          toast('Deleting failed — the service did not respond.');
          return;
        }
        addActivity('info', 'Deleted ' + num(res.body.deleted) + ' records', 'local database cleared');
        state.page = 1;
        return refresh();
      })
      .catch(function () {
        addActivity('failed', 'Delete all failed', 'service not reachable');
      })
      .finally(function () { el.btnDeleteAll.disabled = false; });
  }

  // -------------------------------------------------------------- settings

  function openSettings() {
    json('/api/settings').then(function (res) {
      if (!res.ok || !res.body) { toast('Could not load settings.'); return; }
      var s = res.body;
      state.settings = s;

      el.setHrmis.value = s.hrmisUrl || '';
      el.setPort.value = s.port;
      el.setRemote.checked = !!s.remoteAccessEnabled;
      el.setHttpsPort.value = s.adminHttpsPort;
      el.setAllowedIps.value = (s.allowedIps || []).join(', ');
      el.setDeviceIps.value = (s.deviceAllowedIps || []).join(', ');
      el.setUsername.value = s.username || '';
      el.setCurrentPassword.value = '';
      el.setNewPassword.value = '';
      el.setConfirmPassword.value = '';

      text(el.passwordRule, 'At least ' + (s.minPasswordLength || 12) + ' characters. ' +
        'Leave blank to keep the current password.');
      el.currentPasswordHint.hidden = !s.accountConfigured;

      if (s.certificateError) {
        text(el.settingsWarn, 'Browser access could not start: ' + s.certificateError);
        el.settingsWarn.hidden = false;
      } else {
        el.settingsWarn.hidden = true;
      }

      el.settingsModal.hidden = false;
    });
  }

  function splitList(value) {
    return (value || '').split(',').map(function (v) { return v.trim(); })
      .filter(function (v) { return v.length > 0; });
  }

  function saveSettings() {
    var newPassword = el.setNewPassword.value;
    var confirmPassword = el.setConfirmPassword.value;

    if (newPassword || confirmPassword) {
      if (newPassword !== confirmPassword) {
        showSettingsError('The new passwords do not match.');
        return;
      }
    }

    el.btnSettingsSave.disabled = true;

    // Credentials first: enabling browser access is refused by the service
    // until an account exists, so the order matters on first-time setup.
    var credentialStep = Promise.resolve({ ok: true });
    if (newPassword) {
      credentialStep = json('/api/auth/password', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          username: el.setUsername.value,
          currentPassword: el.setCurrentPassword.value,
          newPassword: newPassword
        })
      });
    }

    credentialStep.then(function (credRes) {
      if (!credRes.ok) {
        showSettingsError((credRes.body && credRes.body.message) || 'Could not update the account.');
        throw new Error('credentials');
      }

      return json('/api/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          hrmisUrl: el.setHrmis.value.trim(),
          port: parseInt(el.setPort.value, 10),
          remoteAccessEnabled: el.setRemote.checked,
          adminHttpsPort: parseInt(el.setHttpsPort.value, 10),
          allowedIps: splitList(el.setAllowedIps.value),
          deviceAllowedIps: splitList(el.setDeviceIps.value)
        })
      });
    }).then(function (res) {
      if (!res.ok) {
        showSettingsError((res.body && res.body.message) || 'Could not save settings.');
        return;
      }
      el.settingsModal.hidden = true;
      addActivity('info', 'Settings saved', 'applied');

      if (newPassword) {
        // The service revokes every session when credentials change, this one
        // included, so send the admin back to the login page deliberately
        // rather than letting the next request bounce them.
        toast('Password changed — signing you in again…');
        setTimeout(function () { window.location.href = '/login'; }, 1200);
        return;
      }
      refresh();
    }).catch(function () {
      /* message already shown */
    }).finally(function () {
      el.btnSettingsSave.disabled = false;
    });
  }

  function showSettingsError(message) {
    text(el.settingsWarn, message);
    el.settingsWarn.hidden = false;
  }

  function logout() {
    json('/api/auth/logout', { method: 'POST' }).finally(function () {
      window.location.href = '/login';
    });
  }

  // ------------------------------------------------------------------ init

  function bind() {
    el = {
      statusChip: $('statusChip'), statusText: $('statusText'),
      statTotal: $('statTotal'), statSynced: $('statSynced'), statUnsynced: $('statUnsynced'),
      search: $('search'), gridBody: $('gridBody'), gridEmpty: $('gridEmpty'),
      activityList: $('activityList'), activityEmpty: $('activityEmpty'),
      pageLabel: $('pageLabel'), btnPrev: $('btnPrev'), btnNext: $('btnNext'),
      btnSyncAll: $('btnSyncAll'), btnDeleteAll: $('btnDeleteAll'),
      btnSettings: $('btnSettings'), btnLogout: $('btnLogout'), userChip: $('userChip'),
      settingsModal: $('settingsModal'), settingsWarn: $('settingsWarn'),
      setHrmis: $('setHrmis'), setPort: $('setPort'), setRemote: $('setRemote'),
      setHttpsPort: $('setHttpsPort'), setAllowedIps: $('setAllowedIps'), setDeviceIps: $('setDeviceIps'),
      setUsername: $('setUsername'), setCurrentPassword: $('setCurrentPassword'),
      setNewPassword: $('setNewPassword'), setConfirmPassword: $('setConfirmPassword'),
      currentPasswordHint: $('currentPasswordHint'), passwordRule: $('passwordRule'),
      btnSettingsSave: $('btnSettingsSave'), btnSettingsCancel: $('btnSettingsCancel'),
      confirmModal: $('confirmModal'), confirmText: $('confirmText'),
      btnConfirmYes: $('btnConfirmYes'), btnConfirmNo: $('btnConfirmNo'),
      toast: $('toast')
    };

    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (tab) {
      tab.addEventListener('click', function () {
        Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (t) {
          t.classList.remove('is-active');
        });
        tab.classList.add('is-active');
        state.filter = tab.getAttribute('data-filter');
        state.page = 1;
        loadPage();
      });
    });

    el.search.addEventListener('input', function () {
      if (searchTimer) clearTimeout(searchTimer);
      searchTimer = setTimeout(function () {
        state.search = el.search.value.trim();
        state.page = 1;
        loadPage();
      }, SEARCH_DEBOUNCE_MS);
    });

    el.btnPrev.addEventListener('click', function () {
      if (state.page > 1) { state.page--; loadPage(); }
    });
    el.btnNext.addEventListener('click', function () {
      if (state.page < state.totalPages) { state.page++; loadPage(); }
    });

    el.btnSyncAll.addEventListener('click', syncAll);
    el.btnDeleteAll.addEventListener('click', askDeleteAll);
    el.btnConfirmYes.addEventListener('click', deleteAll);
    el.btnConfirmNo.addEventListener('click', function () { el.confirmModal.hidden = true; });

    el.btnSettings.addEventListener('click', openSettings);
    el.btnSettingsCancel.addEventListener('click', function () { el.settingsModal.hidden = true; });
    el.btnSettingsSave.addEventListener('click', saveSettings);
    el.btnLogout.addEventListener('click', logout);

    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        el.settingsModal.hidden = true;
        el.confirmModal.hidden = true;
      }
    });
  }

  function start() {
    bind();
    json('/api/auth/status').then(function (res) {
      if (res.ok && res.body && res.body.username) {
        text(el.userChip, 'Signed in as ' + res.body.username);
      }
    });
    refresh();
    startEvents();
    setInterval(refresh, POLL_MS);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
