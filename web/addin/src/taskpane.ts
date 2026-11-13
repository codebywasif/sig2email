// Task pane: lets the sender switch to an alternative approved signature for the current message.
import { API_URL, applySignature, getManualRule, getOptions, setDevToken, setManualRule } from './core';

const $ = (id: string) => document.getElementById(id)!;
const status = (text: string) => { $('status').textContent = text; };

async function choose(ruleId: string | null) {
  status('Updating signature…');
  try {
    await setManualRule(ruleId);
    const result = await applySignature(true);
    status(result === 'disabled' ? 'Signatures are added automatically after sending.' : result === 'none' ? 'No signature applies to this message.' : 'Signature updated.');
    await render();
  } catch (e) {
    status(`Could not update the signature: ${(e as Error).message}`);
  }
}

async function render() {
  const host = $('options');
  try {
    const [options, manual] = await Promise.all([getOptions(true), getManualRule()]);
    host.replaceChildren();
    if (!options.enabled) {
      $('intro').textContent = 'Your signature is added automatically after you send. There is nothing to choose here.';
      return;
    }
    const add = (label: string, ruleId: string | null) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = label;
      button.setAttribute('aria-pressed', String((manual || null) === ruleId));
      button.addEventListener('click', () => void choose(ruleId));
      host.appendChild(button);
    };
    add('Automatic (recommended)', null);
    for (const alt of options.alternatives) add(alt.name, alt.id);
  } catch (e) {
    status(`Sign-in needed or the service is unavailable (${(e as Error).message}).`);
    // Development deployments of the service use their own tokens.
    if (/localhost|127\.0\.0\.1/.test(API_URL)) $('dev').hidden = false;
  }
}

Office.onReady(() => {
  $('saveDev').addEventListener('click', () => {
    void setDevToken(($('devToken') as HTMLInputElement).value.trim()).then(() => { status('Token saved.'); return render(); });
  });
  void render();
});
