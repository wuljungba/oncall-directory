/* global Office, fetch */

Office.onReady(function () {
  loadOnCallStatus();
});

async function loadOnCallStatus() {
  const el = document.getElementById('content');
  try {
    // Get the appointment date from context (or default to today)
    const date = new Date().toISOString().slice(0, 10);

    // Call the backend API for on-call status
    const res = await fetch(`/api/directory/on-call`);
    if (!res.ok) throw new Error('API unreachable');

    const data = await res.json();
    if (!data || data.length === 0) {
      el.innerHTML = `<div class="empty">No one is currently on call</div>`;
      return;
    }

    el.innerHTML = data.map(function (p) {
      var name = (p.firstName || '') + ' ' + (p.lastName || '');
      var role = p.title || '';
      var dept = p.department ? p.department.name : '';
      var status = p.onCallStatus ? 'on-call' : 'off-duty';
      var tier = p.onCallStatus ? 'primary' : '';
      return '<div class="status ' + status + '">' +
        '<div class="name">' + name.trim() + '</div>' +
        (role ? '<div class="detail">' + role + '</div>' : '') +
        (dept ? '<div class="detail">' + dept + '</div>' : '') +
        (tier ? '<div class="badge ' + tier + '">On Call</div>' : '') +
        '</div>';
    }).join('');
  } catch (err) {
    el.innerHTML = '<div class="error">Could not load on-call data. ' + err.message + '</div>';
  }
}
