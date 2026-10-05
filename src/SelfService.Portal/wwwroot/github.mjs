export function setupGitHub({ api, notice }) {
  const $ = id => document.getElementById(id);
  let timer, busy = false;
  async function refresh() {
    clearTimeout(timer);
    try {
      const status = await api('github/status');
      $('github-state').textContent = status.connected ? `Connected as ${status.user} · local GitHub CLI` : status.state;
      $('github-detail').textContent = status.error || (status.available ? (status.waiting ? 'Complete GitHub consent in your browser, then return here. Waiting for verification…' : 'Ready for GitHub browser sign-in or an existing CLI account.') : 'GitHub CLI is missing. Run ./scripts/Install-GitHubCli.ps1 in the repository, then refresh status.');
      $('github-code-panel').hidden = !status.code;
      $('github-code').textContent = status.code || '';
      for (const id of ['github-connect', 'github-attach']) $(id).disabled = busy || status.waiting || !status.available;
      $('github-disconnect').disabled = busy || (!status.connected && !status.waiting);
      $('github-disconnect').textContent = status.waiting ? 'Cancel GitHub sign-in' : 'Disconnect GitHub';
      if (status.waiting) timer = setTimeout(refresh, 1500);
    } catch (e) { $('github-detail').textContent = e.message; }
  }
  for (const [id, route] of [['github-connect', 'connect'], ['github-attach', 'attach'], ['github-disconnect', 'disconnect']]) {
    $(id).addEventListener('click', async () => {
      if (busy) return; busy = true; $(id).disabled = true;
      try { await api(`github/${route}`, {}); document.dispatchEvent(new Event('github-connection-changed')); }
      catch (e) { notice(e.message, true); }
      finally { busy = false; await refresh(); }
    });
  }
  $('github-refresh').addEventListener('click', refresh);
  void refresh();
}
