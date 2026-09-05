(function () {
  'use strict';

  var form = document.getElementById('loginForm');
  var username = document.getElementById('username');
  var password = document.getElementById('password');
  var error = document.getElementById('error');
  var submit = document.getElementById('btnSubmit');

  function showError(message) {
    error.textContent = message;
    error.hidden = false;
  }

  // Already signed in (e.g. the back button landed here) — go straight through.
  fetch('/api/auth/status', { credentials: 'same-origin' })
    .then(function (res) { return res.json(); })
    .then(function (body) {
      if (body && body.authenticated) window.location.href = '/';
    })
    .catch(function () { /* the form still works */ });

  form.addEventListener('submit', function (e) {
    e.preventDefault();
    error.hidden = true;
    submit.disabled = true;

    fetch('/api/auth/login', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'X-Requested-With': 'PglAttendance'
      },
      body: JSON.stringify({ username: username.value, password: password.value })
    })
      .then(function (res) {
        return res.json().catch(function () { return {}; }).then(function (body) {
          if (res.ok) {
            window.location.href = '/';
            return;
          }
          // 429 carries a lockout; anything else is a plain credential failure.
          password.value = '';
          showError(body.error || 'Sign-in failed. Please try again.');
          submit.disabled = false;
        });
      })
      .catch(function () {
        showError('Could not reach the service. Check the connection and try again.');
        submit.disabled = false;
      });
  });
})();
