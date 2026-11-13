// Typed client for the Sig2Email control-plane API.

export type Role = 'Owner' | 'RuleAdmin' | 'Designer' | 'AnalyticsViewer';
export type DeploymentMode = 'ServerSide' | 'ClientSide' | 'Combo';
export type MessageKind = 'New' | 'Reply' | 'Forward';
export type RecipientScope = 'Any' | 'InternalOnly' | 'ExternalAny' | 'ExternalOnly';
export type FilterOp = 'Equals' | 'NotEquals' | 'Contains' | 'StartsWith' | 'IsEmpty' | 'IsNotEmpty';
export type SurveyStyle = 'Emoji' | 'Stars' | 'Thumbs' | 'Custom';

export interface AppConfig {
  /** True until the first owner account has been created. */
  needsSetup: boolean;
  publicBaseUrl: string;
  placeholders: string[];
  specialPlaceholders: string[];
  /** Development builds with demo data only. */
  demo: { password: string; accounts: { username: string; name: string; role: string }[] } | null;
}

export interface Me {
  user: { id: string; name: string; email: string; username: string | null; isManager: boolean; mustChangePassword: boolean };
  tenant: { id: string; name: string; mode: DeploymentMode } | null;
  role: Role | null;
}

export interface TenantInfo {
  id: string; name: string; mode: DeploymentMode; domains: string[]; relayHost: string | null;
  connected: boolean; entraConfigured: boolean; lastSyncAt: string | null; lastSyncError: string | null;
  syncRequested: boolean; smtpHost: string | null; graphPermissions: string;
  counts: { users: number; groups: number; templates: number; rules: number };
}

export interface Account {
  id: string; username: string; displayName: string; email: string | null; role: Role | null; directoryUserId: string | null;
  disabled: boolean; mustChangePassword: boolean; lastLoginAt: string | null; createdAt: string; locked: boolean;
}

export interface CertificateInfo { subject: string; issuer: string; names: string[]; notBefore: string; notAfter: string; thumbprint: string; intermediates: number }

/** Settings → System. Secrets are write-only: the API reports only whether one is stored. */
export interface SystemSettings {
  publicBaseUrl: string;
  entraTenantId: string; entraClientId: string; entraClientSecretSet: boolean; connected: boolean;
  lastSyncAt: string | null; lastSyncError: string | null; graphPermissions: string;
  smtpHostName: string; smtpRequireTls: boolean; smtpRequireClientCertificate: boolean; smtpAllowedClientNames: string[];
  smtpMaxMessageMb: number; certificate: CertificateInfo | null;
  relayPort: number; relayRequireTls: boolean; developmentOverrides: boolean;
  alertMailHost: string; alertMailPort: number; alertMailUser: string; alertMailPasswordSet: boolean; alertMailFrom: string;
  syncIntervalMinutes: number; autoReplyIntervalSeconds: number;
  addinManifestUrl: string; addinRedirectUri: string;
}

export interface DirectoryUser {
  id: string; upn: string; mail: string | null; displayName: string; firstName: string | null; lastName: string | null;
  jobTitle: string | null; department: string | null; company: string | null; office: string | null; phone: string | null;
  mobilePhone: string | null; city: string | null; country: string | null; managerId: string | null; accountEnabled: boolean;
  addresses: string[]; groupIds: string[]; customAttributes: Record<string, string>;
}
export interface DirectoryGroup { id: string; displayName: string; mail: string | null; kind: string; memberCount: number }

export interface TemplateSummary {
  id: string; name: string; description: string | null; imageMode: 'Hosted' | 'Inline'; publishedVersion: number | null;
  hasUnpublishedChanges: boolean; updatedAt: string; updatedBy: string; sourceMode: boolean; ruleCount: number;
}
export interface Template extends Omit<TemplateSummary, 'sourceMode' | 'ruleCount'> { html: string; designJson: string | null }

export interface AttributeFilter { attribute: string; op: FilterOp; value: string | null }
export interface RuleConditions {
  senders: {
    everyone: boolean; userIds: string[]; groupIds: string[]; filters: AttributeFilter[]; filterMatch: 'All' | 'Any';
    excludeUserIds: string[]; excludeGroupIds: string[];
  };
  recipients: { scope: RecipientScope; domains: string[]; exclude: string[] };
  messageTypes: MessageKind[];
  subjectKeywords: string[]; bodyKeywords: string[]; suppressKeywords: string[];
  startUtc: string | null; endUtc: string | null;
}
export interface Rule {
  id: string; name: string; enabled: boolean; priority: number; templateId: string;
  placement: 'Bottom' | 'AboveQuotedText'; channels: 'Server' | 'Client' | 'Both'; onMatch: 'Continue' | 'Stop';
  userSelectable: boolean; trackLinks: boolean; utmSource: string | null; utmMedium: string | null; utmCampaign: string | null;
  conditions: RuleConditions;
}

export interface Survey {
  id: string; name: string; question: string; style: SurveyStyle; customIcons: { imageUrl: string; label: string }[];
  thankYouMessage: string; askComment: boolean; negativeThreshold: number; alertEmails: string[]; webhookUrl: string | null;
  enabled: boolean; maxScore: number;
}

export interface AutoReplyPolicy {
  id: string; name: string; enabled: boolean; userIds: string[]; groupIds: string[]; startUtc: string; endUtc: string;
  internalMessage: string; externalMessage: string; externalAudience: 'None' | 'ContactsOnly' | 'All'; forwardTo: string | null;
  createdByName: string; managerDelegated: boolean;
}

export interface Asset { id: string; fileName: string; contentType: string; size: number; createdAt: string; createdBy: string; url: string }

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

let tokenProvider: () => Promise<string | null> = async () => null;
let onUnauthorized: () => void = () => {};

export function configureApi(getToken: () => Promise<string | null>, unauthorized: () => void) {
  tokenProvider = getToken;
  onUnauthorized = unauthorized;
}

async function request<T>(method: string, path: string, body?: unknown, raw?: 'text'): Promise<T> {
  const headers: Record<string, string> = {};
  const token = await tokenProvider();
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload: BodyInit | undefined;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  let res: Response;
  try {
    res = await fetch(path, { method, headers, body: payload });
  } catch {
    throw new ApiError(0, 'network', 'Cannot reach the Sig2Email service. Check your connection and try again.');
  }
  // A 401 on a request that carried a token means the session ended; without one it is a failed sign-in.
  if (res.status === 401 && token) {
    onUnauthorized();
    throw new ApiError(401, 'unauthorized', 'Your session has expired. Sign in again.');
  }
  if (!res.ok) {
    let code = 'error';
    let message = `Request failed (${res.status}).`;
    try {
      const data = await res.json();
      code = data.error ?? code;
      message = data.message ?? data.detail ?? data.title ?? (data.error ? `Request failed: ${data.error}` : message);
    } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, code, message);
  }
  if (raw === 'text') return (await res.text()) as T;
  if (res.status === 204 || res.status === 202) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  text: (path: string) => request<string>('GET', path, undefined, 'text'),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body ?? {}),
  del: (path: string) => request<void>('DELETE', path),
  upload: <T>(path: string, file: File, fields?: Record<string, string>) => {
    const form = new FormData();
    form.append('file', file);
    for (const [key, value] of Object.entries(fields ?? {})) form.append(key, value);
    return request<T>('POST', path, form);
  },
};

export const emptyConditions = (): RuleConditions => ({
  senders: { everyone: true, userIds: [], groupIds: [], filters: [], filterMatch: 'All', excludeUserIds: [], excludeGroupIds: [] },
  recipients: { scope: 'Any', domains: [], exclude: [] },
  messageTypes: [],
  subjectKeywords: [], bodyKeywords: [], suppressKeywords: [],
  startUtc: null, endUtc: null,
});
