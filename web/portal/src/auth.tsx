import { createContext, useCallback, useContext, useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { Button, Card, Field, Input, MessageBar, MessageBarBody, Spinner, Text, Title2 } from '@fluentui/react-components';
import { api, ApiError, configureApi, type AppConfig, type Me, type Role } from './api';

interface AuthState {
  config: AppConfig;
  me: Me;
  /** Owner passes every check. */
  can: (...roles: Role[]) => boolean;
  reload: () => Promise<void>;
  /** Replaces the session token (after a password change, which ends all older sessions). */
  setToken: (token: string) => Promise<void>;
  signOut: () => void;
}

const AuthContext = createContext<AuthState | null>(null);
export const useAuth = () => useContext(AuthContext)!;

// Session storage: the session ends when the browser tab is closed and is never shared between tabs.
const TOKEN_KEY = 's2e.session';
const readToken = () => { try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; } };
const writeToken = (token: string | null) => {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token);
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch { /* storage unavailable: the session then lasts until the page is reloaded */ }
};
let sessionToken = readToken();
const setSession = (token: string | null) => {
  sessionToken = token;
  writeToken(token);
};

export const MIN_PASSWORD_LENGTH = 10;

export function AuthProvider({ children }: { children: ReactNode }) {
  const [config, setConfig] = useState<AppConfig | null>(null);
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [starting, setStarting] = useState(true);
  const [busy, setBusy] = useState(false);

  const loadMe = useCallback(async () => {
    setMe(await api.get<Me>('/api/me'));
  }, []);

  useEffect(() => {
    configureApi(async () => sessionToken, () => {
      setSession(null);
      setMe(null);
      setNotice('Your session has ended. Sign in again.');
    });
    (async () => {
      try {
        setConfig(await api.get<AppConfig>('/api/config'));
        if (sessionToken) await loadMe();
      } catch (e) {
        if (!(e instanceof ApiError && e.status === 401)) setError((e as Error).message);
      } finally {
        setStarting(false);
      }
    })();
  }, [loadMe]);

  const startSession = useCallback(async (token: string) => {
    setSession(token);
    await loadMe();
  }, [loadMe]);

  const submit = async (action: () => Promise<{ token: string }>) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await startSession((await action()).token);
      setConfig((c) => (c ? { ...c, needsSetup: false } : c));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const value = useMemo<AuthState | null>(() => {
    if (!config || !me) return null;
    return {
      config,
      me,
      can: (...roles) => me.role === 'Owner' || (me.role != null && roles.includes(me.role)),
      reload: loadMe,
      setToken: startSession,
      signOut: () => {
        setSession(null);
        window.location.assign('/');
      },
    };
  }, [config, me, loadMe, startSession]);

  if (starting) return <div className="center-screen"><Spinner label="Loading Sig2Email…" /></div>;
  if (value) return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;

  return (
    <div className="center-screen">
      <Card className="login-card">
        <Title2 as="h1">Sig2Email</Title2>
        {notice && !error && <MessageBar intent="info"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
        {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
        {!config && <Button onClick={() => window.location.reload()}>Try again</Button>}
        {config?.needsSetup && <SetupForm busy={busy} onSubmit={(body) => void submit(() => api.post('/api/auth/setup', body))} />}
        {config && !config.needsSetup && <LoginForm busy={busy} demo={config.demo} onSubmit={(body) => void submit(() => api.post('/api/auth/login', body))} />}
      </Card>
    </div>
  );
}

function LoginForm({ busy, demo, onSubmit }: { busy: boolean; demo: AppConfig['demo']; onSubmit: (body: { username: string; password: string }) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const send = (e: FormEvent) => {
    e.preventDefault();
    if (username.trim() && password) onSubmit({ username, password });
  };
  return (
    <form className="stack" onSubmit={send}>
      <Text>Sign in to manage email signatures, disclaimers, auto-replies and customer feedback.</Text>
      <Field label="User name" required>
        <Input name="username" autoComplete="username" autoFocus value={username} onChange={(_, d) => setUsername(d.value)} />
      </Field>
      <Field label="Password" required>
        <Input name="password" type="password" autoComplete="current-password" value={password} onChange={(_, d) => setPassword(d.value)} />
      </Field>
      <Button appearance="primary" size="large" type="submit" disabled={busy || !username.trim() || !password}>{busy ? 'Signing in…' : 'Sign in'}</Button>
      <Text size={200} className="muted">Forgotten your password? Ask an owner of this portal to reset it.</Text>
      {demo && (
        <div className="stack tight">
          <Text size={200} weight="semibold">Demo accounts (development build)</Text>
          <Text size={200} className="muted">Each one shows a different role. They all use the password <span className="mono">{demo.password}</span>.</Text>
          <div className="demo-accounts">
            {demo.accounts.map((a) => (
              <Button key={a.username} size="small" type="button" disabled={busy} title={`${a.name} — ${a.role}`}
                onClick={() => onSubmit({ username: a.username, password: demo.password })}>{a.username}</Button>
            ))}
          </div>
        </div>
      )}
    </form>
  );
}

function SetupForm({ busy, onSubmit }: { busy: boolean; onSubmit: (body: Record<string, string>) => void }) {
  const [f, setF] = useState({ setupCode: '', organisation: '', displayName: '', username: '', password: '', confirm: '' });
  const set = (key: keyof typeof f) => (_: unknown, d: { value: string }) => setF((x) => ({ ...x, [key]: d.value }));
  const mismatch = f.confirm.length > 0 && f.confirm !== f.password;
  const ready = f.setupCode.trim() && f.organisation.trim() && f.username.trim().length >= 3 && f.password.length >= MIN_PASSWORD_LENGTH && f.password === f.confirm;
  const send = (e: FormEvent) => {
    e.preventDefault();
    if (ready) onSubmit({ setupCode: f.setupCode, organisation: f.organisation, displayName: f.displayName, username: f.username, password: f.password });
  };
  return (
    <form className="stack" onSubmit={send}>
      <Text>Welcome. Create the first administrator account to finish installing Sig2Email. Everything else is configured in the portal afterwards.</Text>
      <Field label="Setup code" required hint="Printed in the service log at start-up and stored in setup-code.txt in the data directory. It proves you have access to the server.">
        <Input value={f.setupCode} onChange={set('setupCode')} autoComplete="off" autoFocus className="mono" />
      </Field>
      <Field label="Organisation name" required><Input value={f.organisation} onChange={set('organisation')} autoComplete="organization" /></Field>
      <Field label="Your name"><Input value={f.displayName} onChange={set('displayName')} autoComplete="name" /></Field>
      <Field label="User name" required hint="At least 3 characters: letters, digits, dot, dash, underscore or @.">
        <Input name="username" value={f.username} onChange={set('username')} autoComplete="username" />
      </Field>
      <Field label="Password" required hint={`At least ${MIN_PASSWORD_LENGTH} characters.`}>
        <Input name="new-password" type="password" value={f.password} onChange={set('password')} autoComplete="new-password" />
      </Field>
      <Field label="Repeat password" required validationState={mismatch ? 'error' : 'none'} validationMessage={mismatch ? 'The passwords do not match.' : undefined}>
        <Input type="password" value={f.confirm} onChange={set('confirm')} autoComplete="new-password" />
      </Field>
      <Button appearance="primary" size="large" type="submit" disabled={busy || !ready}>{busy ? 'Creating…' : 'Create account and sign in'}</Button>
    </form>
  );
}
