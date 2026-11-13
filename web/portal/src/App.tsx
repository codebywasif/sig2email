import { lazy, Suspense, useState, type FormEvent, type ReactElement } from 'react';
import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import {
  Avatar, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuList, MenuPopover,
  MenuTrigger, MessageBar, MessageBarBody, Spinner, Text, Tooltip,
} from '@fluentui/react-components';
import {
  ArrowReplyRegular, BoardRegular, ChartMultipleRegular, DocumentBulletListRegular, EmojiRegular, FlowRegular, MailTemplateRegular,
  BuildingRegular, PeopleRegular, PersonKeyRegular, SettingsRegular,
} from '@fluentui/react-icons';
import { MIN_PASSWORD_LENGTH, useAuth } from './auth';
import { api, type Role } from './api';
import { Empty, Page, useNotify } from './common';
import { Dashboard } from './pages/Dashboard';
import { Templates } from './pages/Templates';
import { Rules } from './pages/Rules';
import { RuleEditor } from './pages/RuleEditor';
import { Directory } from './pages/Directory';
import { AutoReplies } from './pages/AutoReplies';
import { Surveys } from './pages/Surveys';
import { Analytics } from './pages/Analytics';
import { Settings } from './pages/Settings';
import { Accounts } from './pages/Accounts';
import { System } from './pages/System';
import { AuditLog } from './pages/AuditLog';

// The designer pulls in the Monaco editor, so it is loaded on demand.
const TemplateEditor = lazy(() => import('./pages/TemplateEditor'));

interface NavItem { to: string; label: string; icon: ReactElement; roles: Role[] | 'any' | 'tenant-user'; element: ReactElement; extra?: { path: string; element: ReactElement }[] }

const READERS: Role[] = ['RuleAdmin', 'Designer'];

const NAV: NavItem[] = [
  { to: '/', label: 'Overview', icon: <BoardRegular />, roles: 'any', element: <Dashboard /> },
  { to: '/templates', label: 'Templates', icon: <MailTemplateRegular />, roles: READERS, element: <Templates />, extra: [{ path: '/templates/:id', element: <TemplateEditor /> }] },
  { to: '/rules', label: 'Rules', icon: <FlowRegular />, roles: READERS, element: <Rules />, extra: [{ path: '/rules/:id', element: <RuleEditor /> }] },
  { to: '/auto-replies', label: 'Auto-replies', icon: <ArrowReplyRegular />, roles: 'tenant-user', element: <AutoReplies /> },
  { to: '/surveys', label: 'Surveys', icon: <EmojiRegular />, roles: ['RuleAdmin'], element: <Surveys /> },
  { to: '/analytics', label: 'Analytics', icon: <ChartMultipleRegular />, roles: ['RuleAdmin', 'AnalyticsViewer'], element: <Analytics /> },
  { to: '/directory', label: 'Directory', icon: <PeopleRegular />, roles: READERS, element: <Directory /> },
  { to: '/audit', label: 'Audit log', icon: <DocumentBulletListRegular />, roles: ['AnalyticsViewer'], element: <AuditLog /> },
  { to: '/settings', label: 'Organisation', icon: <BuildingRegular />, roles: [], element: <Settings /> },
  { to: '/accounts', label: 'Accounts', icon: <PersonKeyRegular />, roles: [], element: <Accounts /> },
  { to: '/system', label: 'System', icon: <SettingsRegular />, roles: [], element: <System /> },
];

const ROLE_LABEL: Record<Role, string> = {
  Owner: 'Owner', RuleAdmin: 'Rule & policy administrator', Designer: 'Template designer', AnalyticsViewer: 'Analytics viewer',
};

export function App() {
  const { me, can, signOut } = useAuth();

  const allowed = NAV.filter((n) =>
    n.roles === 'tenant-user' ? me.role != null || me.user.isManager : n.roles === 'any' ? me.role != null : can(...n.roles));

  if (allowed.length === 0) {
    return (
      <Shell items={[]}>
        <Page title="No access">
          <Empty title="You do not have an administrative role in Sig2Email">
            Your signature is managed centrally by your organisation. Ask an owner of this portal if you need access.
          </Empty>
          <Button onClick={signOut}>Sign out</Button>
        </Page>
      </Shell>
    );
  }

  return (
    <Shell items={allowed}>
      <Suspense fallback={<Spinner label="Loading…" className="pad" />}>
        <Routes>
          {allowed.flatMap((n) => [
            <Route key={n.to} path={n.to} element={n.element} />,
            ...(n.extra ?? []).map((x) => <Route key={x.path} path={x.path} element={x.element} />),
          ])}
          <Route path="*" element={<Navigate to={allowed[0].to} replace />} />
        </Routes>
      </Suspense>
    </Shell>
  );
}

function Shell({ items, children }: { items: NavItem[]; children: ReactElement | ReactElement[] }) {
  const { me, signOut } = useAuth();
  const [changing, setChanging] = useState(false);
  // Accounts created or reset by an owner carry a temporary password that has to be replaced first.
  const forced = me.user.mustChangePassword;
  return (
    <div className="shell">
      <a className="skip-link" href="#main">Skip to content</a>
      <header className="topbar">
        <div className="brand"><span className="brand-mark" aria-hidden="true">S2</span>Sig2Email{me.tenant && <Text className="tenant-name">{me.tenant.name}</Text>}</div>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content={`${me.user.name} — account menu`} relationship="label">
              <Button appearance="transparent" className="account" icon={<Avatar name={me.user.name} size={28} color="colorful" />}>
                <span className="account-text">{me.user.name}<br /><Text size={100}>{me.role ? ROLE_LABEL[me.role] : me.user.isManager ? 'Manager' : 'No admin role'}</Text></span>
              </Button>
            </Tooltip>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              {me.user.username && <MenuItem onClick={() => setChanging(true)}>Change password</MenuItem>}
              <MenuItem onClick={signOut}>Sign out</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
      </header>
      <div className="body">
        {items.length > 0 && !forced && (
          <nav className="sidenav" aria-label="Main">
            {items.map((n) => (
              <NavLink key={n.to} to={n.to} end={n.to === '/'} className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
                {n.icon}<span>{n.label}</span>
              </NavLink>
            ))}
          </nav>
        )}
        <main id="main" className="content" tabIndex={-1}>
          {forced
            ? <Page title="Choose your own password"><Empty title="You signed in with a temporary password">Replace it with one only you know to continue.</Empty></Page>
            : children}
        </main>
      </div>
      <ChangePasswordDialog open={changing || forced} forced={forced} onClose={() => setChanging(false)} />
    </div>
  );
}

function ChangePasswordDialog({ open, forced, onClose }: { open: boolean; forced: boolean; onClose: () => void }) {
  const { setToken, signOut } = useAuth();
  const notify = useNotify();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const mismatch = confirm.length > 0 && confirm !== next;
  const ready = current.length > 0 && next.length >= MIN_PASSWORD_LENGTH && next === confirm;
  const close = () => { setCurrent(''); setNext(''); setConfirm(''); setError(null); onClose(); };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!ready || busy) return;
    setBusy(true);
    setError(null);
    try {
      // Changing the password ends every other session; this one continues with the fresh token.
      const { token } = await api.post<{ token: string }>('/api/account/password', { currentPassword: current, newPassword: next });
      await setToken(token);
      notify('Password changed');
      close();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} modalType={forced ? 'alert' : 'modal'} onOpenChange={(_, d) => { if (!d.open && !forced) close(); }}>
      <DialogSurface>
        <form onSubmit={(e) => void submit(e)}>
          <DialogBody>
            <DialogTitle>{forced ? 'Choose your own password' : 'Change password'}</DialogTitle>
            <DialogContent className="stack">
              {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
              <Field label={forced ? 'Temporary password' : 'Current password'} required>
                <Input type="password" value={current} onChange={(_, d) => setCurrent(d.value)} autoComplete="current-password" />
              </Field>
              <Field label="New password" required hint={`At least ${MIN_PASSWORD_LENGTH} characters.`}>
                <Input type="password" value={next} onChange={(_, d) => setNext(d.value)} autoComplete="new-password" />
              </Field>
              <Field label="Repeat new password" required validationState={mismatch ? 'error' : 'none'} validationMessage={mismatch ? 'The passwords do not match.' : undefined}>
                <Input type="password" value={confirm} onChange={(_, d) => setConfirm(d.value)} autoComplete="new-password" />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" type="button" onClick={forced ? signOut : close}>{forced ? 'Sign out' : 'Cancel'}</Button>
              <Button appearance="primary" type="submit" disabled={busy || !ready}>Change password</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
