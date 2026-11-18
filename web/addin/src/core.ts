// Shared by the event-based runtime (launchevent.ts) and the task pane (taskpane.ts).
import { createNestablePublicClientApplication, type IPublicClientApplication } from '@azure/msal-browser';

// The Sig2Email service serves these bundles and prepends where it lives and which Entra app to sign in with
// (both are edited in the portal), so the add-in itself is never rebuilt or configured.
const runtime = (globalThis as { __S2E__?: { apiUrl: string; clientId: string } }).__S2E__ ?? { apiUrl: '', clientId: '' };

export const API_URL = runtime.apiUrl;
const CLIENT_ID = runtime.clientId;
const SCOPES = [`api://${CLIENT_ID}/access_as_user`];
const DEV_TOKEN_SETTING = 's2eDevToken';
const SESSION_COMPOSE_KEY = 's2eComposeKey';
const SESSION_MANUAL_RULE = 's2eManualRule';

type ComposeItem = Office.MessageCompose & Office.AppointmentCompose;
const item = () => Office.context.mailbox.item as unknown as ComposeItem;

/** Promise wrapper for Office's callback APIs; resolves undefined on failure so callers can degrade gracefully. */
function ask<T>(call: (cb: (r: Office.AsyncResult<T>) => void) => void): Promise<T | undefined> {
  return new Promise((resolve) => {
    try {
      call((r) => resolve(r.status === Office.AsyncResultStatus.Succeeded ? r.value : undefined));
    } catch {
      resolve(undefined);
    }
  });
}

// ---------- authentication ----------

let msal: Promise<IPublicClientApplication> | undefined;

export async function getToken(interactive = false): Promise<string | null> {
  // Development builds of the service issue their own tokens; the task pane stores one in roaming settings.
  const dev = Office.context.roamingSettings.get(DEV_TOKEN_SETTING) as string | undefined;
  if (dev) return dev;

  // Nested app authentication: Outlook brokers the token, no pop-ups in the common case.
  try {
    msal ??= createNestablePublicClientApplication({ auth: { clientId: CLIENT_ID, authority: 'https://login.microsoftonline.com/common' } });
    const app = await msal;
    try {
      return (await app.acquireTokenSilent({ scopes: SCOPES })).accessToken;
    } catch (e) {
      if (!interactive) throw e;
      return (await app.acquireTokenPopup({ scopes: SCOPES })).accessToken;
    }
  } catch {
    // Legacy Office SSO. Only succeeds if the manifest carries a WebApplicationInfo section, which the shipped one
    // does not; without it this fails quietly and the add-in reports "not signed in".
    try {
      return await Office.auth.getAccessToken({ allowSignInPrompt: interactive, allowConsentPrompt: interactive });
    } catch {
      return null;
    }
  }
}

export function setDevToken(token: string): Promise<void> {
  Office.context.roamingSettings.set(DEV_TOKEN_SETTING, token || undefined);
  return new Promise((resolve) => Office.context.roamingSettings.saveAsync(() => resolve()));
}

async function call<T>(method: string, path: string, body?: unknown, interactive = false): Promise<T> {
  const token = await getToken(interactive);
  if (!token) throw new Error('Not signed in');
  const res = await fetch(API_URL + path, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new Error(`Sig2Email service returned ${res.status}`);
  return (await res.json()) as T;
}

// ---------- message facts ----------

async function recipients(): Promise<string[]> {
  const i = item();
  const fields = [i.to, i.cc, i.bcc, i.requiredAttendees, i.optionalAttendees].filter(Boolean) as Office.Recipients[];
  const lists = await Promise.all(fields.map((f) => ask<Office.EmailAddressDetails[]>((cb) => f.getAsync(cb))));
  return [...new Set(lists.flatMap((l) => l ?? []).map((r) => r.emailAddress).filter(Boolean))];
}

async function messageType(): Promise<'New' | 'Reply' | 'Forward'> {
  const i = item();
  if (!i.getComposeTypeAsync) return 'New';
  const r = await ask<{ composeType: string }>((cb) => i.getComposeTypeAsync(cb as never));
  return r?.composeType === 'reply' ? 'Reply' : r?.composeType === 'forward' ? 'Forward' : 'New';
}

const session = {
  get: (key: string) => (item().sessionData ? ask<string>((cb) => item().sessionData.getAsync(key, cb)) : Promise.resolve(undefined)),
  set: (key: string, value: string) => (item().sessionData ? ask<void>((cb) => item().sessionData.setAsync(key, value, cb)) : Promise.resolve(undefined)),
  remove: (key: string) => (item().sessionData ? ask<void>((cb) => item().sessionData.removeAsync(key, cb)) : Promise.resolve(undefined)),
};

/** Stable id for this compose window, so re-rendering as recipients change reuses one survey link. */
async function composeKey(): Promise<string> {
  let key = await session.get(SESSION_COMPOSE_KEY);
  if (!key) {
    key = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    await session.set(SESSION_COMPOSE_KEY, key);
  }
  return key;
}

// ---------- signature ----------

interface SignatureResponse { enabled: boolean; html: string; ruleIds: string[]; headerName: string; headerValue: string }
export interface AddinOptions { enabled: boolean; mode: string; alternatives: { id: string; name: string }[] }

export const getOptions = (interactive = false) => call<AddinOptions>('GET', '/api/addin/options', undefined, interactive);
export const getManualRule = () => session.get(SESSION_MANUAL_RULE);
export const setManualRule = (ruleId: string | null) => (ruleId ? session.set(SESSION_MANUAL_RULE, ruleId) : session.remove(SESSION_MANUAL_RULE));

/**
 * Asks the service which signature applies to the message as it is right now and puts it into the body.
 * Also records, in an internet header, which rules were applied so the server-side pass does not add them again.
 */
export async function applySignature(interactive = false): Promise<'applied' | 'none' | 'disabled'> {
  const i = item();
  const [to, type, subject, key, manual] = await Promise.all([
    recipients(), messageType(), ask<string>((cb) => i.subject.getAsync(cb)), composeKey(), getManualRule(),
  ]);
  const sig = await call<SignatureResponse>('POST', '/api/addin/signature',
    { recipients: to, messageType: type, subject: subject ?? '', composeKey: key, ruleId: manual || null }, interactive);
  if (!sig.enabled) return 'disabled';

  // Outlook's own signature would otherwise be added on top of the managed one.
  if (i.disableClientSignatureAsync) await ask<void>((cb) => i.disableClientSignatureAsync(cb));
  await ask<void>((cb) => i.body.setSignatureAsync(sig.html, { coercionType: Office.CoercionType.Html }, cb));
  if (i.internetHeaders) await ask<void>((cb) => i.internetHeaders.setAsync({ [sig.headerName]: sig.headerValue || 'rules=' }, cb));
  return sig.ruleIds.length > 0 ? 'applied' : 'none';
}
